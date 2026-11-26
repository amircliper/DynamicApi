"""Run against a built executable/DLL. Python 3 standard library only."""
import concurrent.futures
import json
import os
import pathlib
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = pathlib.Path(__file__).resolve().parent.parent
SOURCE = ROOT / 'src' / 'DynamicApi'
if len(sys.argv) < 2:
    raise SystemExit('Usage: python tests/smoke.py <exe> OR python tests/smoke.py dotnet <dll>')
command = [str(pathlib.Path(a).resolve()) if pathlib.Path(a).is_file() else a for a in sys.argv[1:]]


def assert_equal(a, b):
    assert a == b, (a, b)


def wait_for(fn, seconds=12):
    end = time.monotonic() + seconds
    last = None
    while time.monotonic() < end:
        try:
            if fn():
                return
        except (OSError, AssertionError, ValueError) as exc:
            last = exc
        time.sleep(.2)
    raise AssertionError(f'Timed out: {last}')


with tempfile.TemporaryDirectory(prefix='dynamicapi-test-') as tmp:
    folder = pathlib.Path(tmp)
    shutil.copy(SOURCE / 'schema.json', folder)
    shutil.copy(SOURCE / 'users.json', folder)
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        port = s.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    log = folder / 'server.log'
    process = None

    def start():
        global process
        with log.open('ab') as f:
            process = subprocess.Popen(command + ['--data-dir', tmp, '--port', str(port), '--no-browser'], stdout=f, stderr=subprocess.STDOUT)
        wait_for(lambda: request('GET', '/swagger/v1/swagger.json')[0] == 200)

    def stop():
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()

    def request(method, path, body=None, token=None, headers=None):
        h = dict(headers or {})
        if token:
            h['Authorization'] = 'Bearer ' + token
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode()
            h['Content-Type'] = 'application/json'
        req = urllib.request.Request(base + path, data=body, headers=h, method=method)
        try:
            response = urllib.request.urlopen(req, timeout=10)
        except urllib.error.HTTPError as exc:
            response = exc
        with response:
            data = response.read()
            if 'application/json' in response.headers.get('Content-Type', ''):
                data = json.loads(data)
            return response.status, data

    def expect(status, method, path, body=None, token=None, headers=None):
        actual, data = request(method, path, body, token, headers)
        assert actual == status, (method, path, status, actual, data)
        return data

    def write_schema(data):
        temp = folder / 'schema.next'
        temp.write_text(json.dumps(data) if not isinstance(data, str) else data, encoding='utf-8')
        os.replace(temp, folder / 'schema.json')

    def spec():
        return expect(200, 'GET', '/swagger/v1/swagger.json')

    try:
        start()
        assert_equal(spec()['openapi'], '3.0.3')
        expect(200, 'GET', '/swagger/index.html')
        expect(200, 'GET', '/swagger/swagger-ui-bundle.js')
        assert '/api/products' in spec()['paths']
        assert_equal(expect(200, 'GET', '/api/products')['totalCount'], 0)
        expect(400, 'POST', '/api/products', {'price': 'wrong'})
        expect(400, 'POST', '/api/products', {'id': 3})
        expect(400, 'POST', '/api/products', {'image': '/api/files/' + 'a'*32 + '.png'})
        p = expect(201, 'POST', '/api/products', {'title': 'Laptop', 'price': 12, 'isActive': True})
        assert_equal(p['id'], 1)
        expect(201, 'POST', '/api/products', {'title': 'Phone', 'price': 22, 'isActive': False})
        assert_equal(expect(200, 'GET', '/api/products?search=lap&filter.isActive=true')['totalCount'], 1)
        assert_equal(expect(200, 'GET', '/api/products?sort=price&order=desc&pageSize=1')['items'][0]['title'], 'Phone')
        assert_equal(expect(200, 'PATCH', '/api/products/1', {'price': 15})['title'], 'Laptop')
        assert 'isActive' not in expect(200, 'PUT', '/api/products/1', {'title': 'Laptop', 'price': 16})
        expect(204, 'DELETE', '/api/products/2')
        expect(404, 'GET', '/api/products/2')
        expect(403, 'POST', '/api/products', {'title': 'evil'}, headers={'Origin': 'https://evil.example'})

        def create(i):
            return expect(201, 'POST', '/api/categories', {'title': str(i)})['id']
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            ids = list(pool.map(create, range(24)))
        assert_equal(len(set(ids)), 24)
        assert_equal(expect(200, 'GET', '/api/categories?pageSize=100')['totalCount'], 24)
        assert (folder / 'App_Data/Data/categories.json.bak').is_file()

        seed = expect(200, 'POST', '/api/auth/login', {'username': 'demo', 'password': '123456'})
        assert seed['accessToken']
        expect(401, 'POST', '/api/auth/login', {'username': 'demo', 'password': 'wrong'})
        expect(201, 'POST', '/api/auth/register', {'username': 'alice', 'password': 'secret123'})
        expect(409, 'POST', '/api/auth/register', {'username': 'ALICE', 'password': 'secret123'})
        token = expect(200, 'POST', '/api/auth/login', {'username': 'alice', 'password': 'secret123'})['accessToken']
        assert_equal(expect(200, 'GET', '/api/auth/me', token=token)['username'], 'alice')
        assert 'secret123' not in (folder / 'App_Data/users.json').read_text()
        expect(401, 'GET', '/api/auth/me')

        schema = json.loads((folder / 'schema.json').read_text())
        schema['auth'] = True
        schema['notes'] = {'properties': {'text': 'string!'}}
        write_schema(schema)
        wait_for(lambda: '/api/notes' in spec()['paths'])
        assert spec()['paths']['/api/notes']['post']['security']
        expect(401, 'GET', '/api/products')
        expect(401, 'GET', '/api/products', token='garbage')
        expect(400, 'POST', '/api/notes', {}, token)
        expect(201, 'POST', '/api/notes', {'text': 'hello'}, token)
        write_schema('{broken')
        time.sleep(1.3)
        assert '/api/notes' in spec()['paths']
        expect(200, 'GET', '/api/notes', token=token)
        schema['notes']['properties'] = {'label': 'string'}
        write_schema(schema)
        wait_for(lambda: 'label' in spec()['components']['schemas']['notes.Record']['properties'])
        assert 'text' not in expect(200, 'GET', '/api/notes/1', token=token)
        expect(200, 'PATCH', '/api/notes/1', {'label': 'new'}, token)
        assert 'hello' in (folder / 'App_Data/Data/notes.json').read_text()
        del schema['notes']
        write_schema(schema)
        wait_for(lambda: '/api/notes' not in spec()['paths'])
        expect(404, 'GET', '/api/notes', token=token)
        assert (folder / 'App_Data/Data/notes.json').exists()

        boundary = 'test-' + uuid.uuid4().hex
        parts = []
        def field(name, data, filename=None):
            head = f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"'
            if filename:
                head += f'; filename="{filename}"\r\nContent-Type: application/octet-stream'
            parts.append(head.encode() + b'\r\n\r\n' + data + b'\r\n')
        field('title', b'File product')
        field('image', b'image-content', 'image.png')
        field('attachments', b'one', 'one.txt')
        field('attachments', b'two', 'two.txt')
        multipart = b''.join(parts) + f'--{boundary}--\r\n'.encode()
        up = expect(201, 'POST', '/api/products', multipart, token, {'Content-Type': f'multipart/form-data; boundary={boundary}'})
        assert_equal(len(up['attachments']), 2)
        assert_equal(expect(200, 'GET', up['image'], token=token), b'image-content')
        expect(401, 'GET', up['image'])
        assert_equal(expect(200, 'GET', up['attachments'][0], token=token), b'one')
        assert_equal(len(list((folder / 'App_Data/Uploads').iterdir())), 3)
        # Upload failure must not leave new files behind.
        bad = multipart.replace(b'name="title"', b'name="unknown"')
        expect(400, 'POST', '/api/products', bad, token, {'Content-Type': f'multipart/form-data; boundary={boundary}'})
        assert_equal(len(list((folder / 'App_Data/Uploads').iterdir())), 3)
        # A valid multipart body with a bad field type reaches storage validation after staging.
        bad_price = (f'--{boundary}\r\nContent-Disposition: form-data; name="price"\r\n\r\n"wrong"\r\n'.encode() + multipart)
        expect(400, 'POST', '/api/products', bad_price, token, {'Content-Type': f'multipart/form-data; boundary={boundary}'})
        assert_equal(len(list((folder / 'App_Data/Uploads').iterdir())), 3)
        expect(400, 'PATCH', '/api/products/1', {'image': up['image']}, token)
        expect(200, 'PATCH', '/api/products/' + str(up['id']), {'image': None}, token)

        stop()
        start()
        expect(200, 'GET', '/api/products/1', token=token)
        assert_equal(expect(200, 'GET', '/api/categories?pageSize=100', token=token)['totalCount'], 24)
        assert_equal(expect(200, 'GET', '/api/auth/me', token=token)['username'], 'alice')
        print('PASS: CRUD, query, concurrent writes, auth, Swagger, schema reload, files, persistence.')
    except BaseException:
        print(log.read_text(encoding='utf-8', errors='replace'), file=sys.stderr)
        raise
    finally:
        stop()
