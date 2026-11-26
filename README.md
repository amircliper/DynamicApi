# DynamicApi

یک Backend آموزشی برای فرانت‌اندکارها. بدون Controller، دیتابیس خارجی، Node.js یا کدنویسی Backend. فقط یک فایل JSON و Swagger.

## ۱. اجرا (برای فرانت‌اندکار)

بعد از دریافت نسخه منتشرشده Windows x64، فایل ZIP را Extract کنید. `DynamicApi.exe` را اجرا کنید. Swagger در مرورگر باز می‌شود:

`http://127.0.0.1:5050/swagger/index.html`

نیازی به نصب SDK، Runtime، Visual Studio، IIS یا اینترنت برای اجرای نسخه Self-contained نیست. پوشه باید قابل نوشتن باشد. برای بستن سرور Ctrl+C بزنید. برای تغییر پورت، در PowerShell بنویسید: `./DynamicApi.exe --port 5051`. اجرای هم‌زمان دو برنامه با یک پوشه داده پشتیبانی نمی‌شود.

## ۲. ساخت API جدید

فایل `schema.json` کنار برنامه را باز و ذخیره کنید:

```json
{
  "auth": false,
  "products": {
    "properties": {
      "title": "string!",
      "price": "number",
      "isActive": "boolean",
      "image": "file",
      "attachments": "files"
    }
  },
  "categories": {
    "properties": { "title": "string" }
  }
}
```

با Refresh کردن Swagger، APIهای جدید را می‌بینید؛ برنامه را نبندید. نام `products` نام API است. `id` عددی و خودافزایشی است و خود برنامه می‌سازد. نام Resource باید با حرف کوچک انگلیسی شروع شود و می‌تواند حروف کوچک انگلیسی، عدد، خط تیره و زیرخط داشته باشد. نام‌های `users`، `auth`، `files` و نام‌های رزروشده ویندوز مجاز نیستند. نام property با حرف انگلیسی یا زیرخط شروع می‌شود؛ حروف انگلیسی، عدد و زیرخط مجازند. `id` رزرو شده است. نام فیلد را در درخواست دقیقاً مانند schema بنویسید.

| نوع | مثال |
|---|---|
| `string` | `"Mobile"` |
| `number` | `150.5` |
| `integer` | `12` |
| `boolean` | `true` |
| `date` | `"2026-11-26"` |
| `datetime` | `"2026-11-26T12:00:00Z"` |
| `object` | `{"color":"red"}` |
| `array` | `[1,2,3]` |
| `file` | انتخاب یک فایل |
| `files` | انتخاب چند فایل |

با `!` در انتهای نوع (مثل `string!`) فیلد اجباری می‌شود. سایر فیلدها اختیاری و قابل مقداردهی null هستند. نوع‌های `integer` و `number` به ترتیب در بازه Int64 و Decimal دات‌نت هستند. رشته خالی یا آرایه خالی در فیلد اجباری پذیرفته نمی‌شود.

## ۳. CRUD

| Method | URL | نتیجه |
|---|---|---|
| GET | `/api/products` | لیست صفحه‌بندی شده |
| GET | `/api/products/1` | یک رکورد |
| POST | `/api/products` | ساخت (201) |
| PUT | `/api/products/1` | جایگزینی تمام فیلدهای فعلی (200) |
| PATCH | `/api/products/1` | تغییر فقط فیلدهای ارسالی (200) |
| DELETE | `/api/products/1` | حذف فیزیکی رکورد (204) |

برای ساخت، از `application/json` استفاده کنید:

```json
{ "title": "Laptop", "price": 1200, "isActive": true }
```

برای تغییر فقط قیمت، PATCH بزنید و `{"price":1000}` بفرستید. در PUT فیلدهای فعلی که ارسال نشده‌اند از رکورد حذف می‌شوند. فیلدهای اجباری باید در نتیجه نهایی معتبر باشند. فیلد تعریف‌نشده و `id` در Body مجاز نیست.

مثال لیست:

`GET /api/products?page=1&pageSize=10&search=lap&sort=price&order=desc&filter.isActive=true`

- `page` از ۱ شروع می‌شود؛ `pageSize` پیش‌فرض ۲۰ و حداکثر ۱۰۰۰.
- `search` فقط فیلدهای string را بدون حساسیت به بزرگی حروف جستجو می‌کند.
- `sort` نام یک فیلد scalar یا `id` است؛ `order` مقدار `asc` یا `desc` دارد.
- `filter.<name>=value` تطابق دقیق با یک مقدار است؛ چند فیلتر با AND ترکیب می‌شوند. رشته‌ها بدون حساسیت به بزرگی حروف مقایسه می‌شوند.
- برای `array`، `object`، `file` و `files` مرتب‌سازی و فیلتر نداریم.

```json
{
  "items": [{"id": 1, "title": "Laptop", "price": 1200}],
  "page": 1,
  "pageSize": 10,
  "totalCount": 1,
  "totalPages": 1
}
```

خطاها معمولاً به شکل `{"message":"..."}` هستند. 400 ورودی اشتباه، 401 نیاز به Login، 404 پیدا نشد، 409 نام کاربری تکراری، 413 حجم زیاد، 415 نوع Content-Type اشتباه.

## ۴. Login و Bearer Token

برای اجباری شدن Login در تمام APIهای داده و فایل فقط `auth` را در `schema.json` برابر `true` کنید. این تغییر هم Hot Reload می‌شود. همه کاربران احراز هویت‌شده دسترسی یکسان دارند؛ Role و Permission نداریم.

کاربر نمونه در `users.json`:

```json
[{ "username": "demo", "password": "123456" }]
```

کاربران دستی این فایل هنگام شروع برنامه وارد ذخیره داخلی می‌شوند (فقط کاربر جدید؛ کاربران موجود بازنویسی نمی‌شوند). برای افزودن دستی کاربر جدید برنامه را یک‌بار دوباره اجرا کنید یا از Register استفاده کنید. فایل seed رمز نمونه را به‌صورت متن ساده نگه می‌دارد، اما رمز ثبت‌شده در `App_Data/users.json` فقط Hash است. از رمز واقعی خودتان استفاده نکنید. پس از اولین ورود seedها می‌توانید فایل `users.json` را به `[]` تغییر دهید.

- `POST /api/auth/register` با Body `{"username":"ali","password":"123456"}` کاربر جدید ایجاد می‌کند.
- `POST /api/auth/login` همان Body را می‌گیرد و `accessToken`، `tokenType`، `expiresIn`، `expiresAt` و اطلاعات کاربر می‌دهد.
- `GET /api/auth/me` با توکن، کاربر جاری را برمی‌گرداند؛ حتی اگر `auth:false` باشد، این API توکن می‌خواهد.

توکن JWT برای ۸ ساعت معتبر است. Refresh Token و Logout سمت سرور نداریم. برای خروج، توکن را در فرانت حذف کنید. Register و Login همیشه عمومی هستند. نام کاربری تکراری (بدون حساسیت به بزرگی حروف) پذیرفته نمی‌شود.

در Swagger ابتدا Login کنید و `accessToken` را کپی کنید؛ سپس دکمه Authorize را بزنید و فقط مقدار توکن (بدون کلمه Bearer) را وارد کنید. در فرانت‌اند:

```js
const response = await fetch('http://127.0.0.1:5050/api/products', {
  headers: { Authorization: `Bearer ${token}` }
});
const result = await response.json();
```

## ۵. آپلود فایل

در هر Resource می‌توانید `file` یا `files` تعریف کنید. Swagger برای آن Resource گزینه `multipart/form-data` و ورودی فایل نمایش می‌دهد. لازم نیست Endpoint جداگانه بسازید. در فرانت‌اند:

```js
const data = new FormData();
data.append('title', 'Laptop');
data.append('price', '1200');
data.append('image', imageInput.files[0]);
for (const file of attachmentsInput.files) data.append('attachments', file);

const response = await fetch('http://127.0.0.1:5050/api/products', {
  method: 'POST',
  headers: token ? { Authorization: `Bearer ${token}` } : {},
  body: data
});
```

برای FormData، Content-Type را دستی تنظیم نکنید. مقادیر `object` و `array` در فرم باید JSON.stringify شوند. برای PATCH، فیلد فایل ارسال‌نشده بدون تغییر می‌ماند؛ فایل‌های جدید جایگزین مجموعه فایل همان فیلد می‌شوند. برای حذف لینک فایل اختیاری از رکورد، PATCH با JSON مقدار `null` (یا `[]` برای `files`) بفرستید. URL فایل قبلی همان رکورد و همان فیلد را می‌توان در درخواست JSON نگه داشت؛ URL دلخواه یا فایل رکورد دیگر پذیرفته نمی‌شود.

فایل‌ها در `App_Data/Uploads` با نام یکتای تولیدشده ذخیره می‌شوند. مقدار ذخیره‌شده در JSON، URL نسبی مانند `/api/files/9d124271a18846baa3e9247cb28d1596.png` است. برای نمایش تصویر در حالت `auth:false`، URL سرور را به ابتدای آن اضافه کنید. در حالت `auth:true`، چون تگ img نمی‌تواند Authorization header ارسال کند، از fetch با توکن و Blob استفاده کنید:

```js
const response = await fetch('http://127.0.0.1:5050' + product.image, {
  headers: { Authorization: `Bearer ${token}` }
});
if (!response.ok) throw new Error('Download failed');
const imageUrl = URL.createObjectURL(await response.blob());
// image.src = imageUrl; پس از پایان استفاده URL.revokeObjectURL(imageUrl)
```

حداکثر حجم هر فایل ۲۰ MiB، مجموع فایل‌ها در یک درخواست ۵۰ MiB و حداکثر تعداد ۵۰ است. نام اصلی کاربر مستقیماً برای مسیر فایل استفاده نمی‌شود. تصاویر رایج raster قابل نمایش‌اند؛ سایر فایل‌ها به صورت دانلود برمی‌گردند. حذف رکورد یا تعویض فایل، فایل فیزیکی قبلی را عمداً خودکار حذف نمی‌کند؛ ممکن است لینک قبلی در داده‌های آموزشی یا Backup لازم باشد. فایل‌های قدیمی همچنان از URL قبلی با سیاست احراز هویت سراسری قابل دریافت‌اند. پاکسازی دستی فقط بعد از توقف برنامه و بررسی ارجاع‌ها انجام شود.

## ۶. Hot Reload و اطلاعات

هر ۵۰۰ میلی‌ثانیه فایل schema بررسی می‌شود. با ذخیره فایل معتبر، Snapshot مشترک API و OpenAPI اتمیک جایگزین می‌شود؛ Swagger را Refresh کنید. اگر JSON یا Schema اشتباه باشد، آخرین نسخه معتبر فعال می‌ماند و علت در Console نوشته می‌شود. در اولین اجرا باید schema معتبر باشد. درخواست جاری با Snapshot زمان شروع خودش تمام می‌شود.

اضافه/حذف Resource و فیلد، تغییر نوع و تغییر auth قابل انجام است. حذف یا تغییر نام در Schema داده قبلی را پاک نمی‌کند؛ API فقط فیلدهای فعلی را نمایش می‌دهد. اگر نوع فیلدی تغییر کند، داده قدیمی خودکار تبدیل نمی‌شود و ممکن است تا زمان اصلاح، با نوع جدید همخوان نباشد. برای PUT/PATCH نتیجه نهایی طبق Schema فعلی اعتبارسنجی می‌شود. حذف Resource از schema آن را از Swagger و API حذف می‌کند اما فایل داده آن باقی می‌ماند.

```text
DynamicApi.exe
schema.json
users.json
App_Data/
  Data/
    products.json
    categories.json
  Uploads/
  users.json
  jwt-key.json
```

فایل داده هر Resource هنگام اولین ثبت ایجاد می‌شود. ساختار آن `nextId` و آرایه `items` است. برنامه نوشتن‌ها را سریال می‌کند و فایل را با Atomic Replace جایگزین می‌کند؛ نسخه قبلی در `.bak` نگهداری می‌شود. برای بازیابی، برنامه را متوقف کرده و پس از بررسی، فایل سالم Backup را جایگزین کنید. بازیابی خودکار انجام نمی‌شود. فایل داخلی داده را در حین کار برنامه دستی ویرایش نکنید. برای Reset کامل، بعد از بستن برنامه از App_Data نسخه پشتیبان بگیرید و سپس پوشه را حذف کنید؛ کاربر seed دوباره ساخته می‌شود.

## ۷. ساخت نسخه اجرایی (فقط توسعه‌دهنده)

روی سیستم توسعه‌دهنده به .NET 10 SDK و دسترسی NuGet نیاز است. برای Visual Studio، نسخه‌ای با پشتیبانی .NET 10 و workload توسعه ASP.NET نصب کنید.

### اجرای پروژه در Visual Studio

فایل `DynamicApi.sln` را از ریشه باز کنید. پروژه `DynamicApi` را Startup Project قرار دهید، تنظیمات را روی Debug و پروفایل `DynamicApi` بگذارید و F5 یا Ctrl+F5 بزنید. خود برنامه Swagger را باز می‌کند. در این حالت `src/DynamicApi/schema.json` مستقیماً خوانده می‌شود و تغییرات آن بدون Build مجدد اعمال می‌شود؛ فایل کاربران seed و `App_Data` توسعه نیز در همین مسیر هستند. برای اجرا در Release یا نسخه منتشرشده، پیش‌فرض پوشه کنار فایل اجرایی است؛ آرگومان `--data-dir` در هر دو حالت اولویت دارد.

```text
DynamicApi/
  DynamicApi.sln
  README.md
  publish.ps1
  src/
    DynamicApi/
      DynamicApi.csproj
      Program.cs
      Properties/launchSettings.json
      Schema/
      Storage/
      Files/
      Documentation/
      schema.json
      users.json
  tests/smoke.py
```

### انتشار نسخه مستقل

از ریشه Solution در PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

خروجی در `artifacts/DynamicApi-win-x64` قرار می‌گیرد؛ آن پوشه را ZIP کنید و به دانشجو بدهید، نه سورس. Swagger UI داخل بسته برنامه است و از CDN استفاده نمی‌کند. فایل‌های `schema.json` و `users.json` عمداً کنار EXE باقی می‌مانند.

اجرای سورس در محیط توسعه از ریشه Solution:

```powershell
dotnet run --project src/DynamicApi -- --data-dir src/DynamicApi --no-browser
```

تست‌های یکپارچه از طریق Python 3 (فقط برای توسعه‌دهنده، بدون پکیج اضافی):

```powershell
dotnet build DynamicApi.sln -c Release
python tests/smoke.py dotnet src/DynamicApi/bin/Release/net10.0/DynamicApi.dll
# یا روی EXE منتشرشده:
python tests/smoke.py artifacts/DynamicApi-win-x64/DynamicApi.exe
```

تست از پوشه موقت و پورت موقت استفاده می‌کند و اطلاعات واقعی را تغییر نمی‌دهد.

## ۸. محدوده استفاده

این پروژه برای آموزش و توسعه محلی طراحی شده است: تنها روی IPv4 loopback گوش می‌دهد، HTTP دارد و CORS برای originهای localhost / 127.0.0.1 روی پورت‌های مختلف فعال است. Frontend را با localhost اجرا کنید، نه file:// یا IP شبکه. برای استفاده Production یا اشتراک شبکه طراحی نشده است. فایل‌ها و داده‌ها محدود به حافظه/دیسک یک سیستم هستند؛ دیتابیس JSON برای داده‌های کم‌حجم آموزشی مناسب است، نه حجم بالا یا چند سرور همزمان. روابط بین Resourceها، پنل مدیریت، Refresh Token، نقش‌ها، و Custom Endpointهای خارج CRUD در این نسخه عمداً وجود ندارند.
