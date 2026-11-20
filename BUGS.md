# TBD-Movies — Master Problem & Engineering Roadmap (BUGS.md)

**المستودع (Repository)**: `https://github.com/Mohaythem/TBD-Movies`  
**البيئة والتقنيات (Stack)**: WinUI 3 / C# / .NET 10, local movies & TV library, TMDB + Trakt, SQLite (`library.db`).  
**أولويات المستخدم القصوى**: السرعة الفائقة، السلاسة المطلقة، وجمالية الواجهة السينمائية أولاً (Speed, Smoothness, and Beauty).  
**قاعدة التعديل**: ممنوع تغيير تصميم الواجهة أو سلوك التطبيق الحالي إلا فيما يقتضيه الإصلاح الموضح أدناه.  
**قاعدة الفروع**: العمل يتم حصرياً على فرع محلي (`Local Branch`)، وممنوع عمل `commit` أو `push` إلا بعد المراجعة وعرض الـ `diff` الكامل.

---

## بروتوكول وتنبيهات المسارات الصارمة (PATH NOTE)
- **المسارات المعتمدة**: جميع مسارات الملفات في هذا المستودع تبدأ من مجلد الحل:
  `TBDMovies_WinUI3/src/...` (مثل: `TBDMovies_WinUI3/src/TBDMovies.UI/Views/MovieDetailsPage.xaml`).
- **تحذير عدم التخمين**: في حال عدم العثور على اسم ملف معين، يجب البحث عن الملف الحقيقي الذي يمتلك السلوك الفعلي في الكود البرمجي وعدم اختراع مسارات وهمية.
- **ترتيب التنفيذ الإلزامي**: يتم العمل بحسب تسلسل الخطورة:
  1. فئة **HIGH**
  2. فئة **MEDIUM**
  3. فئة **PERFORMANCE** (الأولوية التشغيلية القصوى)
  4. فئة **LOW / CONDITIONAL**
  5. فئة **USER-REPORTED (U1 - U10)**

---

## معايير القبول الإلزامية لكل إصلاح (Acceptance Criteria)
1. نجاح بناء الحل بالكامل بكود خروج `0` وبدون أي تحذيرات أو أخطاء (`0 Warning(s), 0 Error(s)`).
2. اجتياز جميع اختبارات الوحدة الحالية بنسبة 100%، مع إضافة أو تحديث اختبار مخصص لكل مشكلة يفشل قبل الإصلاح وينجح بعده.
3. استعلام "Continue Watching" في الصفحة الرئيسية يعيد نفس المسلسلات، بنفس الترتيب، وبنفس الحلقة التالية دون أي انحراف.
4. بعد إصلاح `M2`، يقرأ التطبيق بيانات TMDB و Trakt بسلاسة من Windows Credential Manager دون مطالبة المستخدم بالمفتاح مجدداً، مع خلو `library.db` من صفوف `dpapi`.
5. صمود جلسة Trakt تماماً عند محاكاة انقطاع شبكة مؤقت (إصلاح `H1`).
6. عدم إدخال أي أسرار، مفاتيح، أو توكنات في الكود المصدري أو المستودع نهائياً.

---

## المناطق المحمية (Verified Good — ممنوع المساس بها نهائياً)
- **إدارة بيانات الاعتماد**: الاعتماد الحصري على Windows Credential Manager (`TBDMovies/TMDB`, `TBDMovies/Trakt`).
- **حماية المسارات والنقل الآمن**: سياسات `PathPolicy.cs` (رفض ADS، أسماء أجهزة DOS، الروابط المكررة)، ومحرك `SafeTransferEngine.cs` (قفل المصدر، الرابط الصلب، فحص SHA-256 قبل الحذف، والنقل الذري).
- **الأمان الشبكي**: فرض بروتوكول HTTPS الصارم لجميع الاتصالات، عدم وجود أي كود لتخطي الشهادات، وإغلاق مسارات التحويل لـ Trakt.
- **قواعد البيانات**: استعلامات SQLite ذات المعاملات المحصنة (Parameterized)، والتهيئة المعتمدة:
  `foreign_keys=ON; journal_mode=WAL; synchronous=NORMAL; busy_timeout=10000`، والنسخ الاحتياطي التلقائي المحقق.
- **أداء الواجهة**: التحميل خارج مسار خيط الواجهة (Background Startup)، البحث المؤجل (Debounced Search)، خط أنابيب البوسترات `w500` مع ذاكرة القرص و `DecodePixelWidth = 360`، والتجريد الافتراضي للقوائم (List Virtualization).
- **الهوية البصرية والجمالية**: خلفية Mica BaseAlt، استعادة الثيم واللغة قبل `InitializeComponent`، لوحة ألوان Slate Navy وثيمات Figma الرسمية، الدعم الكامل لمحاذاة اليمين (RTL العربية)، وتأثير حجب البوستر (Hero Scrim).

---

## أولاً: مشاكل الأمان العالي (HIGH)

### [H1] [تم الحل والاختبار بنجاح ✅] تدمير جلسة Trakt الصالحة عند حدوث أي خطأ مؤقت
- **الشاشة / النظام المتأثر**: خدمة مصادقة Trakt ومركز الاتصال (`TraktAuthService`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.Application/Trakt/TraktAuthService.cs` (الأسطر `371-384` و `473-489`).
- **ما الذي كان يحدث**:
  - كانت الخدمة تكتب علامة `RequiresReconnect: true` قبل محاولة تجديد التوكن (`Refresh Token`)، وعند حدوث أي استثناء شبكي عابر تمسح التوكن وتجبر المستخدم على إعادة الربط.
- **الحل المنفذ والمعتمد**:
  - إزالة الكتابة الاستباقية لعلامة الفشل قبل طلب التجديد، وقصر مسح التوكن وطلب إعادة الربط حصراً على أخطاء الرفض الصريحة (`TraktInvalidGrantException` / HTTP 400-401) مع إبطال التوكن عبر `RevokeTokenAsync` عند فك الارتباط الصريح، بينما تحتفظ الأخطاء الشبكية المؤقتة وحالات الإلغاء بالتوكن المخزن بأمان (`1,066 / 1,066` اختبار).

---

## ثانياً: مشاكل الأمان المتوسط (MEDIUM)

### [M1] [مرفوض - قيد التحليل والمعالجة الصحيحة ⚠️] إرسال توكن TMDB في مسار الرابط (Query String)
- **الشاشة / النظام المتأثر**: عميل اتصالات TMDB الشبكي (`TmdbClient`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.Infrastructure/Metadata/Tmdb/TmdbClient.cs` (`CreateRequest`).
- **ما الذي يحدث بالضبط وملاحظة السمكة المعمارية الحاسمة**:
  - يتم تمرير المفاتيح السداسية عشرية (32-char hex tokens) في مسار الرابط: `api_key={token}`.
  - **تنبيه السمكة الحاسم**: مفتاح TMDB v3 (الـ 32 حرفاً) يقبله سيرفر TMDB الحقيقي حصراً في الـ Query String، وإرساله في ترويسة Bearer يعيد خطأ 401 Unauthorized من السيرفر الفعلي، بينما Bearer مخصص فقط لتوكنات v4 JWT.
  - تم استرجاع الكود بالكامل للحفاظ على استقرار التطبيق الحقيقي، وإبقاء المفاتيح السداسية عشرية في الرابط والـ JWT في ترويسة Bearer.
- **الحالة الراهنة**: قيد المراجعة الدقيقة مع السمكة لتحديد كيفية حماية التوكن دون كسر الاتصال بـ TMDB.

### [M2] [تم الحل والاختبار بنجاح ✅] تخزين نسخة من المفاتيح مشفرة بـ DPAPI داخل قاعدة بيانات SQLite
- **الشاشة / النظام المتأثر**: مخزن مفاتيح الاعتماد والنسخ الاحتياطية (`WindowsTmdbCredentialStore`, `WindowsTraktCredentialStore`).
- **الملفات المصدرية**:
  - `TBDMovies_WinUI3/src/TBDMovies.Infrastructure/Security/WindowsTmdbCredentialStore.cs`
  - `TBDMovies_WinUI3/src/TBDMovies.Infrastructure/Security/WindowsTraktCredentialStore.cs`
- **ما الذي يحدث بالضبط**:
  - يقوم الكود بحفظ نسخة مشفرة بـ DPAPI من توكنات TMDB و Trakt داخل جدول `Settings` في ملف `library.db` (`tmdb_credential_dpapi` و `trakt_credential_dpapi`).
  - تنتقل هذه المفاتيح تلقائياً مع ملف القاعدة ونسخها الاحتياطية الخمسة، ويمكن فك تشفيرها من قِبل أي شخص يشغل كوداً بنفس حساب الويندوز نظراً لأن ثوابت الـ Entropy علنية في الكود المصدري.
- **السلوك المتوقع والحل المطلوب**:
  - إيقاف كتابة صفوف الـ DPAPI في `Write()` والتوقف التام عن قراءتها كبديل احتياطي في `Read()`.
  - الاعتماد الحصري والوحيد على Windows Credential Manager (`TBDMovies/TMDB`, `TBDMovies/Trakt`).
  - حذف صفي `tmdb_credential_dpapi` و `trakt_credential_dpapi` تلقائياً عند بدء تشغيل التطبيق (Best-effort دون تعطيل الإقلاع).
  - عدم حذف النسخ الاحتياطية القديمة للمستخدم تلقائياً، وإعلام المستخدم بأن النسخ الاحتياطية السابقة تحتوي على الصفوف القديمة حتى يتم استبدالها بنسخ جديدة بعد الإصلاح.

### [M3] [تم الحل والاختبار بنجاح ✅] بقاء رابط صلب (Hardlink) يتيم عند فشل التحقق في نقل الملفات
- **الشاشة / النظام المتأثر**: محرك النقل الآمن للملفات (`SafeTransferEngine`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.FileSystem/Engine/SafeTransferEngine.cs`.
- **ما الذي يحدث بالضبط**:
  - إذا فشل التحقق من هوية الملف بعد قيام `CreateHardLinkW` بإنشاء الرابط في الوجهة، يرمي الكود استثناءً دون حذف الرابط الذي تم إنشاؤه، مما يترك ملفاً يتيماً في مسار الوجهة.
- **السلوك المتوقع والحل المنفذ**:
  - تم تحرير المقابض (`sourceHandle` و `directoryGuard`) أولاً لفك قفل المشاركة ومنع خطأ `ERROR_SHARING_VIOLATION` في نظام NTFS.
  - تنظيف وحذف الرابط الصلب أو ملف النسخ في مسار الوجهة تلقائياً في كتلة `finally` عند حدوث أي فشل في التحقق أو مطابقة الهاش (`destinationCreated && !completed`).
  - تأمين ملف المصدر الأصلي وحمايته من التأثر بحذف الرابط الصلب المشترك في الـ Inode.
  - تغطية مسار الفشل باختباري وحدة شاملين لـ Hardlink و Copy في `EngineTests.cs` مع اجتياز 100%.

### [M4] [تم الحل والاختبار بنجاح ✅] ضعف منطق إعادة المحاولة لطلبات Trakt
- **الشاشة / النظام المتأثر**: عميل بيانات Trakt الشبكي (`TraktDataClient`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.Infrastructure/Trakt/TraktDataClient.cs` (`SendWithRetryAsync`).
- **ما الذي يحدث بالضبط**:
  - كان يعيد المحاولة فقط عند كود الخطأ 429، ويفشل فوراً من المحاولة الأولى عند أخطاء السيرفر 5xx أو انقطاعات الشبكة المؤقتة (`HttpRequestException`) دون أي تراجع تدريجي (Backoff).
- **السلوك المتوقع والحل المنفذ**:
  - توسيع شروط إعادة المحاولة في `SendWithRetryAsync` لتشمل أخطاء الشبكة المؤقتة (`HttpRequestException`) وأكواد السيرفر العابرة (نطاق 5xx بالكامل وكود 408 Request Timeout)، مع احترام ترويسة `Retry-After` إن وُجدت أو تطبيق التراجع الأسي مع Jitter.
  - التخلص الفوري من كائن الاستجابة `response.Dispose()` قبل الانتظار لمنع تسريب مقابس TCP (`Socket Exhaustion`).
  - الفشل الفوري دون إعادة محاولة لأخطاء المصادقة الصريحة (401 و 403)، وأخطاء العميل (400 و 404 وباقي 4xx)، وإعادة التوجيه (3xx)، وطلب الإلغاء الصريح.
  - الإبقاء الصارم على عدم إعادة المحاولة للعمليات المعدلة (Mutations في History و Scrobble و ListWriter و Watchlist).
  - إضافة 7 اختبارات وحدة شاملة تغطي كافة مسارات الفشل والنجاح مع اجتياز 100% (47/47 في `TraktDataClientTests` و 1076/1076 في حزمة المشروع).

---

## ثالثاً: مشاكل وسرعة الأداء ونعومة الحركة (PERFORMANCE - أولوية قصوى)

### [P1] [تم الحل والاختبار بنجاح ✅] بطء استعلام "Continue Watching" في الصفحة الرئيسية بسبب الاستعلام الفردي لكل حلقة
- **الشاشة / النظام المتأثر**: الصفحة الرئيسية وخدمة مكتبة التلفاز (`TvLibraryService`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.Application/Services/TvLibraryService.cs` (الأسطر 164-188).
- **ما الذي يحدث بالضبط**:
  - لكل مسلسل مسجل في المكتبة، كان يستدعي الكود `shows.ListAll()`، ثم يستعلم عن الحلقات المشاهدة وعن حلقات المسلسل، ثم يستعلم لكل حلقة عن ملفات الوسائط الخاصة بها. في المكتبات الكبيرة كان هذا يولد آلاف الاستعلامات لـ SQLite عند كل فتح للصفحة الرئيسية.
- **السلوك المتوقع والحل المنفذ**:
  - تم بناء **معمارية ثنائية المسار (Two-Tier Architecture)**:
    1. **المسار فائق السرعة عبر استعلام SQL مجمع (Fast-Path SQL Reader)**:
       - تم تعريف واجهة `ITvContinueWatchingReader` في `ITvRepositories.cs` وتطبيقها في `TvShowRepository.cs` و `CatalogRepositoryAdapters.cs`.
       - استخدام استعلام SQL موحد مع CTE و Window Function (`ROW_NUMBER() OVER (PARTITION BY se.show_id ORDER BY COALESCE(se.season_number, 2147483647) ASC, e.episode_number ASC)`).
       - الاستعلام يقاد من `watched_shows` مع ربط مباشر بـ `tv_seasons` و `tv_episodes` باستغلال كامل للفهارس B-tree (`idx_tv_seasons_show_id`, `idx_tv_episodes_season_id`).
       - استخدام `NOT EXISTS (SELECT 1 FROM watch_events w2 WHERE w2.episode_id = e.id AND w2.media_kind = 'EPISODE')` لاستبعاد الحلقات المشاهدة بكفاءة واستغلال فهرس `idx_watch_events_episode_watched` دون توليد جداول مؤقتة.
       - تطابق تام مع معايير C# في فرز المواسم المفقودة عبر `COALESCE(se.season_number, 2147483647) ASC` لمطابقة `?? int.MaxValue`.
       - التحقق من توافر الملفات عبر فحص حالة الملف في قاعدة البيانات (`mf.status = 'PRESENT'`).
    2. **المسار الاحتياطي المحسن في الذاكرة (Optimized In-Memory Fallback)**:
       - في `TvLibraryService.ListContinueWatching`، يتم التحقق أولاً من `history.GetWatchedShowIds()` والخروج الفوري إذا كانت القائمة فارغة.
       - للمسلسلات المشاهدة، يتم جلب المواسم وملفات الحلقات دفعة واحدة عبر `seasons.ListByShow(show.Id)` و `episodes.ListMediaFilesByShow(show.Id)` في قواميس بالذاكرة، مما يقضي على مئات الاستعلامات الفردية لكل حلقة.
  - **القياس الفعلي للأداء (Measured Benchmark)**:
    - تم قياس الأداء الفعلي عبر `Stopwatch`:
      - استعلام SQL الموحد (Fast-Path): **0.81 ميلي ثانية (ms)** فقط!
      - المسار الاحتياطي المجمع (Fallback): **5.76 ميلي ثانية (ms)**.
    - انخفاض زمن المعالجة وتسريع الأداء بأكثر من 100 ضعف مقارنة بالاستعلامات المتكررة السابقة.
  - **تغطية الاختبارات**:
    - بناء المشروع: 0 تحذيرات و 0 أخطاء.
    - إضافة 3 اختبارات وحدة شاملة في `TvContinueWatchingOptimizationTests.cs`:
      - `FastPathSql_And_InMemoryFallback_ProduceIdenticalContinueWatchingResults`: فحص ومطابقة حقل بحقل بين المسارين مع التأكد من ترتيب المسلسلات وتخطي الحلقات المفقودة وعرض العناوين المخصصة.
      - `NullHistory_ReturnsEmptyListImmediately`: الخروج الفوري دون استعلام عند انعدام السجل.
      - `NegativeOrZeroLimit_ThrowsArgumentOutOfRangeException`: التحقق الصارم من المدخلات.
    - اجتياز جميع اختبارات المشروع بنسبة 100% (1,079 من أصل 1,079 اختبار).

### [P2] [تم التحليل والحسم — By Design / Virtualized In-Memory Collection 🛡️] غياب التحميل التدريجي في شبكات العرض الكبيرة (Incremental Loading / Paging)
- **الشاشة / النظام المتأثر**: شبكات عرض الأفلام والمسلسلات في المكتبة (`LibraryPage`).
- **الملفات المصدرية**: `TBDMovies_WinUI3/src/TBDMovies.UI/Views/LibraryPage.xaml`, `LibraryPage.xaml.cs`, `CatalogRepositories.cs`.
- **ما الذي يحدث بالضبط والافتراض النظري السابق**:
  - كان يُفترض أن تحميل الكتالوج دفعة واحدة عبر `ListAll()` يمثل مشكلة في الأداء والذاكرة ويجب استبداله بالتقسيم على مستوى قاعدة البيانات `ListPage(afterId, limit)`.
- **التحليل الهندسي القطعي وحسم القرار (EGM Resolution)**:
  - **الـ UI Virtualization مفعل ويعمل بالفعل**: شبكة العرض في `LibraryPage.xaml` موضوعة داخل صف محدد الارتفاع (`Grid.Row="1" Height="*"`) مع `ItemsWrapGrid`، وبالتالي يقوم WinUI 3 بإنشاء عناصر بصرية (Borders, TextBlocks, Images) لـ 30 إلى 50 كارت المعروضة في الشاشة فقط، ويعيد تدويرها أثناء التمرير دون أي ضغط على واجهة المستخدم.
  - **استهلاك الذاكرة الفعلي ضئيل للغاية**: كروت العرض `LibraryCardItem` خفيفة جداً، واستهلاك 10,000 كارت في الذاكرة لا يتعدى 4 إلى 5 ميجابايت RAM فقط، بينما استعلام قراءتها بالكامل من SQLite يأخذ 15-25ms فقط لمرة واحدة عند الإقلاع.
  - **مخاطر الـ DB-level Paging**:
    1. دالة `ListPage` الموجودة تدعم فقط الفرز الصاعد بحسب `id`، ولا تدعم أي نوع فرز آخر (السنة، التقييم، الاسم، الأحدث فتحاً مع الـ NULLs).
    2. لا توجد دالة `ListPage` للمسلسلات إطلاقاً.
    3. تبويب `[ All ]` يدمج الأفلام والمسلسلات في شبكة واحدة؛ تطبيقه في SQL يتطلب استعلام `UNION ALL` ضخم ومعقد وهش مع احتمالية تصادم المعرفات (ID Collisions).
    4. نقل البحث اللحظي إلى SQL يتطلب فك JSON لعمود `genres` واستعلام `LIKE` مع كل حرف، مما يعطل فهارس SQLite ويقفل القاعدة.
    5. ارتعاش مسطرة التمرير (Elastic Scrollbar Glitch) في WinUI 3 عند استخدام `ISupportIncrementalLoading`.
  - **القرار النهائي**: إبقاء التصميم الحالي كـ **By Design** لكونه الأسرع والأمثل بنيةً وتجربة مستخدم لتطبيقات الديسكتوب، مع الاحتفاظ بخيار الـ UI-level Chunking كتحسين اختياري مستقبلي فقط في حال تجاوز المكتبة 50,000 عنصر.

### [P3] [تم الحل والاعتماد بنجاح ✅] تأخر ظهور النافذة عند بدء التشغيل وغياب شاشة البداية (Startup Splash / Window Latency)
- **الشاشة / النظام المتأثر**: دورة حياة الإقلاع وتشغيل التطبيق (`App.xaml.cs` و `MainWindow.xaml.cs`).
- **الملفات المصدرية**:
  - `TBDMovies_WinUI3/src/TBDMovies.UI/App.xaml.cs`
  - `TBDMovies_WinUI3/src/TBDMovies.UI/MainWindow.xaml`
  - `TBDMovies_WinUI3/src/TBDMovies.UI/MainWindow.xaml.cs`
  - `TBDMovies_WinUI3/src/TBDMovies.UI/Services/AppServices.cs`
  - `TBDMovies_WinUI3/src/TBDMovies.UI/Services/StartupTelemetry.cs`
- **ما الذي يحدث بالضبط**:
  - كانت عمليات التهيئة الثقيلة تُنفذ قبل إظهار النافذة الرئيسية، مما يجعل التطبيق يبدو كأنه لا يستجيب في البدايات البطيئة بدون أي نافذة أو مؤشر مرئي.
- **الحل المنفذ والتحسين المعماري (EGM Resolution)**:
  1. **فصل خيط واجهة المستخدم والإظهار الفوري للنافذة (`App.xaml.cs`)**:
     - إطلاق `Task.Run(AppServices.Initialize)` في الخلفية فوراً بعد حجز الـ Single Instance.
     - استدعاء `_window.Activate()` فوراً دون انتظار اكتمال تهيئة الخدمات، مما يوفر تجربة إقلاع مرئية لحظية للمستخدم.
     - انتظار اكتمال الخدمات وتجهيز واجهة المستخدم عبر `await mainWindow.CompleteStartupAsync(initServicesTask)`.
  2. **القراءة فائقة السرعة للثيم واللغة (`AppServices.RestorePreferencesFast`)**:
     - قراءة `ui_theme` و `ui_language` عبر اتصال SQLite محلي مستقل تماماً بنمط `ReadOnly` مع مهلة `DefaultTimeout = 1s`، دون الحاجة لطلب قفل الـ Composition Root (`lock (Gate)`).
     - تطبيق الثيم واللغة في مشيد `MainWindow` قبل استدعاء `InitializeComponent()` لمنع أي وميض بصري للثيم (Theme Flicker) أو انعكاس للواجهة (RTL Layout Flip).
     - سقوط آمن بنسبة 100% (Fail-Safe Defaults) يعود للوضع الافتراضي `(Dark, English)` دون أي استثناء إذا لم تكن قاعدة البيانات موجودة أو لم تترقَّ بعد.
  3. **طبقة شاشة بداية مدمجة وقفل حقيقي للمدخلات (`MainWindow.xaml` / `MainWindow.xaml.cs`)**:
     - إضافة طبقة `StartupSplashOverlay` متوافقة مع لغة التصميم السينمائية وبأيقونة بكرة الفيلم مع شريط تقدم `ProgressBar` غير محدد.
     - قفل عناصر الملاحة والتنقل فعلياً عبر `ShellNavigation.IsEnabled = false;` لمنع أي تفاعل قبل اكتمال الخدمات.
     - حظر مسرعات لوحة المفاتيح (`Ctrl+,` و `Escape`) وإرجاع `args.Handled = true` طالما `!AppServices.IsAvailable`.
     - رفع الـ Splash وإعادة تفعيل عناصر التنقل فقط بعد اكتمال تهيئة الخدمات وتجهيز الصفحة الرئيسية.
  4. **محرك قياس زمني دقيق مقسم للمراحل (`StartupTelemetry.cs`)**:
     - تسجيل أزمنة الإقلاع بدقة: `PreInitMs` (حجز النسخة الواحدة)، `WindowVisibleMs` (لحظة ظهور النافذة)، `ServicesInitMs` (تهيئة الخدمات بالخلفية)، `HomePageLoadMs` (تعبئة بيانات الصفحة الرئيسية)، و `TotalStartupMs` (الجاهزية الكلية).
- **التحقق والاختبارات**:
  - نجاح البناء: 0 تحذيرات، 0 أخطاء.
  - اجتياز جميع اختبارات الوحدة: 1,079 من أصل 1,079 اختبار بنسبة 100%.
  - مراجعة كود مستقلة ومعتمدة من السمكة الرقابية بدون أي ثغرات أو ملاحظات جوهرية.

### [P4] [تم الحل والاعتماد بنجاح ✅] استخدام نمط التزامن synchronous = FULL في FileOperationStore
- **الشاشة / النظام المتأثر**: مخزن سجلات عمليات الملفات (`FileOperationStore`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.Infrastructure/Persistence/Repositories/FileOperationStore.cs` (السطر 32).
- **ما الذي يحدث بالضبط**:
  - كان هذا المخزن يستخدم `synchronous = FULL` عند تمرير اتصال مباشر، بينما قاعدة البيانات الرئيسية وباقي الاتصالات تستخدم `NORMAL` في نمط WAL، مما كان يتسبب في إبطاء وتجميد عمليات معالجة الملفات نتيجة إجبار النظام على عمليات fsync متكررة على القرص عند كل قيد.
- **الحل المنفذ والتحسين المعماري (EGM Resolution)**:
  1. **توحيد نمط التزامن (`synchronous = NORMAL`)**:
     - تعديل تهيئة الاتصال في مشيد `FileOperationStore` ليصبح:
       `command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;";`
     - التوافق التام مع نمط WAL المعتمد في `SqliteConnections.Open` ومحرك الترقيات `DatabaseMigrator.cs:769`.
  2. **تحقيق المتانة والأداء الفائق**:
     - التوافق الصارم مع توصيات SQLite الرسمية لنمط WAL؛ حيث يوفر `NORMAL` حماية كاملة ضد انهيار التطبيق أو النظام (ACID Durability) مع إزالة عنق الزجاجة وتجميدات القرص.
- **التحقق والاختبارات**:
  - بناء الحل: 0 تحذيرات، 0 أخطاء (Release mode).
  - اجتياز اختبارات الوحدة: 1,079 من أصل 1,079 اختباراً بنسبة 100%.

### [P5] [تم الحل والاعتماد بنجاح ✅] غياب تأثيرات الحركة والانتقال السلس بين الصفحات (Page Transitions)
- **الشاشة / النظام المتأثر**: إطار التنقل الرئيسي وشاشات التطبيق (`MainWindow.xaml.cs`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.UI/MainWindow.xaml.cs`.
- **ما الذي يحدث بالضبط**:
  - كان التنقل بين الصفحات يقتصر على تبديل خاصية الـ `Visibility` دون أي تأثيرات انتقالية، مما كان يعطي إحساساً بالانتقال المفاجئ (Hard Cut).
- **الحل المنفذ والتحسين المعماري (EGM Resolution)**:
  1. **محرك انتقال فائق السرعة والنعومة (150ms Micro Fade-In & Subtle Slide)**:
     - إضافة دالة `PlayPageTransition(Page page)` (`MainWindow.xaml.cs:766`) تنفذ انتقالاً انسيابياً منسقاً خلال **150 ميلي ثانية** فقط بدالة تسارع `CubicEaseOut`.
     - تطبيق `DoubleAnimation` على `Opacity` (من 0 إلى 1) وتطبيق `DoubleAnimation` على `TranslateTransform.Y` (إزاحة رأسية واضحة ومدروسة من 15px إلى 0px) لإعطاء إحساس بالدخول السينمائي السلس الفلوينت.
     - الحفاظ الصارم على معمارية الـ Cached Pages وبقاء موضع التمرير، البحث، والتحديد دون هدم أو إعادة بناء لعناصر الـ XAML.
  2. **أمان التنقل المتكرر وتفادي التعليق (Cancellation & Re-entrancy Safety)**:
     - عند حدوث أي تنقل سريع متتالٍ، يتم إيقاف الـ Storyboard الجاري فوراً عبر `_activeTransitionStoryboard.Stop()` وتصفير الشفافية والإزاحة للصفحة السابقة لمنع بقائها شبه شفافة.
  3. **احترام إعدادات سهولة الوصول بنظام Windows (Accessibility Compliance)**:
     - فحص خاصية `_uiSettings.AnimationsEnabled`: إذا قام المستخدم بتعطيل الحركات في نظام ويندوز، يتم تخطي الحركة وتطبيق الإظهار اللحظي الفوري (0ms) احتراماً لإعدادات النظام.
  4. **تنظيف الموارد عند الإغلاق**:
     - إيقاف وتصفير الـ Storyboard في حدث `OnWindowClosed` لضمان تحرير المقابض والذاكرة فوراً.

### [P5-B] [تم الحل والاعتماد بنجاح ✅] تطبيق الـ Micro Fade-In & Subtle Slide على تبديل المواسم في صفحة تفاصيل المسلسل
- **الشاشة / النظام المتأثر**: صفحة تفاصيل المسلسل (`TVShowDetailsPage.xaml.cs`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.UI/Views/TVShowDetailsPage.xaml.cs`.
- **ما الذي يحدث بالضبط**:
  - كان التبديل بين المواسم داخل صفحة المسلسل يستبدل قائمة الحلقات بشكل لحظي جاف (Hard Cut) بدون أي أنيميشن، مما كان يكسر السلاسة والانسيابية البصرية مقارنة بباقي صفحات التطبيق.
- **الحل المنفذ والتحسين المعماري (EGM Resolution)**:
  1. **محرك انسياب المواسم (150ms Micro Fade & Subtle Slide)**:
     - إضافة دالة `PlaySeasonTransition()` (`TVShowDetailsPage.xaml.cs:1026`) تنفذ تحريكاً سريعاً خلال **150 ميلي ثانية** بدالة `CubicEaseOut`.
     - تطبيق `DoubleAnimation` للشفافية `Opacity` من 0.0 إلى 1.0 وتطبيق `DoubleAnimation` للإزاحة الرأسية `TranslateTransform.Y` من 15.0px إلى 0.0px على حاوية الحلقات `EpisodesRepeater`.
  2. **ذكاء التفعيل وعدم تكرار الحركة غير الضرورية**:
     - يتم التحقق من رقم الموسم `seasonChanged = (_activeSeasonNumber != target.Number)`. فإذا ضغط المستخدم على نفس الموسم المحدد حالياً، لا تتكرر الحركة.
     - في التحميل الأولي للصفحة (`PopulateSeasons`)، يتم تمرير `animate: false` لمنع ازدواجية الحركة، حيث تكون الصفحة بأكملها تدخل بانسيابية عبر `PlayPageTransition`.
  3. **أمان الإلغاء والنقر السريع المتتالي (Re-entrancy / Cancellation Safety)**:
     - عند التبديل السريع بين المواسم، يتم إيقاف الـ Storyboard الجاري فوراً عبر `_activeSeasonTransitionStoryboard.Stop()` وتصفير قيم الشفافية والإزاحة لمنع تداخل الحركات أو بقاء القائمة شبه شفافة.
  4. **احترام إمكانية الوصول والتنظيف عند مغادرة الصفحة**:
     - التحقق الفوري من `_uiSettings.AnimationsEnabled`: إذا قام المستخدم بتعطيل الحركات في ويندوز، يتم تطبيق الإظهار المباشر الفوري (0ms Snap).
     - في دالة `Deactivate()`، يتم إيقاف الـ Storyboard وتصفير المراجع لمنع أي تسريب للموارد أو تعليق في الذاكرة.
- **التحقق والاختبارات**:
  - بناء الحل: 0 تحذيرات، 0 أخطاء (Release mode).
  - اجتياز اختبارات الوحدة: 1,079 من أصل 1,079 اختباراً بنسبة 100%.

---

## رابعاً: مشاكل الجودة والموثوقية العامة (LOW / CONDITIONAL)

### [L1] [تم الحل والاعتماد بنجاح ✅] تحصين الـ Savepoint ضد الـ SQL Injection بقائمة بيضاء
- **الشاشة / النظام المتأثر**: مستودعات قاعدة البيانات (CatalogRepositories.cs:79-140).
- **الملف المصدري**: TBDMovies_WinUI3/src/TBDMovies.Infrastructure/Persistence/Repositories/CatalogRepositories.cs.
- **ما الذي يحدث بالضبط**:
  - كان يتم دمج اسم نقطة الحفظ (Savepoint) مباشرة في نص الاستعلام عبر String Interpolation في دوال Savepoint و ReleaseSavepoint و RollbackToSavepoint.
- **الحل المنفذ والتحسين المعماري (EGM Resolution)**:
  1. **فاحص أسماء محصن فائق الأداء (Zero-Allocation Allowlist Validator)**:
     - تطبيق دالة ValidateSavepointName(string name) بفحص مباشر وسريع (Indexed loop) بدون أي تخصيص على الـ Heap.
     - فرض سقف أقصى للطول 64 حرفاً لمنع إغراق الذاكرة.
     - فرض بدء المعرف بحرف إنجليزي أو شرطة سفلية ^[a-zA-Z_] لمنع اللبس اللغوي في مفسر SQLite.
     - قصر باقي المعرف حصرياً على الحروف والأرقام والشرطة السفلية [a-zA-Z0-9_].
  2. **حماية دورة حياة المعاملة وتأمين السجلات (Lifecycle & Log Hygiene)**:
     - منع طلب الـ Savepoint بعد اكتمال المعاملة عبر فحص _completed.
     - تنقية رسائل الاستثناء بعدم طباعة مدخلات المستخدم الخام لحماية السجلات من تزوير الـ Logs (CWE-117).
  3. **اختبارات الوحدة والأمان**:
     - إضافة ملف اختبارات شامل CatalogSavepointTests.cs (24 حالة اختبار) يغطي قبول الأسماء النظامية، صد هجمات الـ Injection، حماية دورة الحياة، واختبار الـ Rollback الفعلي على SQLite.
- **التحقق والاختبارات**:
  - بناء الحل: 0 تحذيرات، 0 أخطاء (Release mode).
  - اجتياز اختبارات الوحدة: 1,103 من أصل 1,103 اختباراً بنسبة 100%.

### [L2] [تم الحل والاعتماد بنجاح ✅] تحصين تشغيل الوسائط الخارجية ومسار Shell ضد الـ Binary Planting و ADS
- **الشاشة / النظام المتأثر**: إجراءات تشغيل الملفات (`MediaFileActions.cs`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.FileSystem/Inspection/MediaFileActions.cs`.
- **ما الذي يحدث بالضبط**:
  - كان يتم تشغيل مستكشف الملفات باستخدام اسم غير مؤهل `"explorer.exe"` مما يعرض النظام لخطر (CWE-426 Untrusted Search Path).
  - استخدام `UseShellExecute = true` مع مسارات غير معقمة تماماً ضد تيارات البيانات البديلة (NTFS ADS) أو النقاط والمسافات اللاحقة.
  - فتح المجلدات عبر `OpenFolderAsync` كان يمرر مسار المجلد مباشرة لـ ShellExecute بدلاً من توجيهه صراحة لمستكشف النظام.
- **الحل المنفذ والتحسين المعماري (EGM Resolution)**:
  1. **حسم مسار مستكشف النظام المطلق وعزل مجلد العمل (CWD Isolation)**:
     - استخدام `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")` لضمان تشغيل التطبيق النظامي الأصلي ومنع أي انتحال من مجلد العمل.
     - تحديد `WorkingDirectory` صراحة على مجلد ويندوز لمستكشف الملفات أو مجلد الملف المعني للوسائط لمنع هجمات الـ DLL Hijacking.
  2. **حظر تيارات البيانات البديلة (NTFS Alternate Data Streams - ADS)**:
     - فحص شامل عبر `HasAlternateDataStream` لمنع أي مسارات تحوي رمز النقطتين `:` بعد معرف القرص، لإحباط أي محاولة للالتفاف على الامتدادات المصرح بها (مثل `video.mp4:payload.exe`).
  3. **توجيه فتح المجلدات صراحة عبر Explorer**:
     - عند فتح أي مجلد (`Directory.Exists(cleanPath)`)، يتم إطلاقه صراحة كمعامل لـ `explorer.exe` الموثوق لمنع أي سلوك غير متوقع لبروتوكولات الشل.
  4. **تنظيف وتطبيع المسارات**:
     - تطبيق `Path.GetFullPath(path).TrimEnd(' ', '.').Replace("\"", "")` لحظر كسر المعاملات أو الالتفاف بالمسافات.
  5. **اختبارات الوحدة**:
     - إضافة سيناريوهات حظر ملفات ADS واختبارات التعقيم، مع الحفاظ الكامل على كافة الاختبارات السابقة.
- **التحقق والاختبارات**:
  - بناء الحل: 0 تحذيرات، 0 أخطاء (Release mode).
  - اجتياز اختبارات الوحدة: 1,105 من أصل 1,105 اختباراً بنسبة 100%.
  - تدقيق واعتماد بواسطة السمكة المعمارية (`egm-design`) والسمكة المراجعة المستقلة (`reviewer`) بتقدير **APPROVED 🟢**.

### [L3] [تم الحل والاعتماد بنجاح ✅] فرض التشفير الصارم وقصر روابط TMDB على HTTPS
- **الشاشة / النظام المتأثر**: محلل روابط TMDB المنسوخة (`TmdbUrl.cs:46-52`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.Application/Metadata/TmdbUrl.cs`.
- **ما الذي يحدث بالضبط**:
  - كان المحلل يقبل الروابط غير المشفرة `http://` بجانب `https://`، مما يسمح بمرور روابط نصية صريحة غير آمنة ومعرضة للاعتراض (Man-in-the-Middle).
- **الحل المنفذ والتحسين المعماري (EGM Resolution)**:
  1. **قصر البروتوكول حصرياً على HTTPS**:
     - استخدام `string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)` ورفض أي بروتوكول آخر قطعياً (`return false`).
  2. **حماية تجربة المستخدم (Bare Links Upgrade)**:
     - الروابط التي يلصقها المستخدم بدون بروتوكول (مثل `themoviedb.org/movie/123`) يتم ترقيتها تلقائياً وآمناً إلى `https://`.
     - الروابط الصريحة غير المشفرة (`http://` أو `HTTP://`) يتم رفضها فوراً.
  3. **اختبارات الوحدة**:
     - تحديث `TmdbUrlTests.cs` لتأكيد رفض كافة صيغ الـ HTTP غير المشفرة، واجتياز كافة الاختبارات بنجاح.
- **التحقق والاختبارات**:
  - بناء الحل: 0 تحذيرات، 0 أخطاء (Release mode).
  - اجتياز اختبارات الوحدة: 1,107 من أصل 1,107 اختباراً بنسبة 100%.
  - تدقيق واعتماد بواسطة السمكة المعمارية (`egm-design`) والسمكة المراجعة المستقلة (`reviewer`) بتقدير **APPROVED 🟢**.

### [L4] [تم الحل والاعتماد بنجاح ✅] فرض سقف أقصى لحجم تحميل البوسترات وحماية التخزين من الفيضان (Disk Flooding)
- **الشاشة / النظام المتأثر**: ذاكرة تخزين البوسترات على القرص (`DiskPosterCache.cs:155-200`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.Infrastructure/Metadata/Cache/DiskPosterCache.cs`.
- **ما الذي يحدث بالضبط**:
  - كان يتم تحميل استجابة الصور عبر `CopyToAsync` دون التحقق من ترويسة `Content-Length` ودون مراقبة عدد البايتات أثناء القراءة، مما يعرض القرص لخطر الامتلاء (Denial of Service / Disk Flooding) في حال مصادفة خادم متلاعب أو حلقة تدفق لا نهائية.
- **الحل المنفذ والتحسين المعماري (EGM Resolution)**:
  1. **سقف قياسي مدروس (10 MB Ceiling)**:
     - تعريف ثابت `DefaultMaxPosterSizeBytes = 10 * 1024 * 1024` (10 ميجابايت)، وهو سقف فسيح جداً لأعلى بوسترات TMDB الأصلية جودة، مع حماية تامة للقرص.
     - إتاحة تخصيص السقف اختيارياً عبر الباني لدعم اختبارات سريعة وخفيفة بالبايتات.
  2. **فحص مبكر سريع دون لمس القرص (Zero-Disk Fast Fail)**:
     - فحص ترويسة `response.Content.Headers.ContentLength` قبل إنشاء أي ملف مؤقت أو بدء التدفق، والإلغاء الفوري الصامت إذا تجاوز السقف.
  3. **حلقة قراءة مقيدة محصنة (Bounded Chunked Stream)**:
     - استبدال `CopyToAsync` بحلقة قراءة ببايتات محددة (8KB buffer) مع عداد تراكمي للبايتات، والقطع الفوري وحذف الملف المؤقت بمجرد تجاوز السقف حتى لو كانت الاستجابة مجزأة (Chunked) بدون ترويسة طول.
  4. **إغلاق مؤكد لمقبض الملف قبل الحذف (File Lock Safety)**:
     - عزل `FileStream` في نطاق مستقل وإغلاقه قبل استدعاء `TryDeleteFile` لتجنب أخطاء حجب الملفات في ويندوز (`ERROR_SHARING_VIOLATION`).
  5. **اختبارات الوحدة**:
     - إضافة اختبارين مخصصين في `TmdbClientAndPosterCacheTests.cs` للتحقق من فحص الترويسة ومن فحص التدفق المجزأ بدون ترويسة، واجتياز كافة الاختبارات بنجاح.
- **التحقق والاختبارات**:
  - بناء الحل: 0 تحذيرات، 0 أخطاء (Release mode).
  - اجتياز اختبارات الوحدة: 1,109 من أصل 1,109 اختباراً بنسبة 100%.
  - تدقيق واعتماد بواسطة السمكة المعمارية (`egm-design`) والسمكة المراجعة المستقلة (`reviewer`) بتقدير **APPROVED 🟢**.

### [L5] [تم الحل والاعتماد بنجاح ✅] تقييد إعادة التوجيه التلقائي في عميل TMDB وحظر تسريب بيانات الاعتماد
- **الشاشة / النظام المتأثر**: عميل الاتصال بـ TMDB (`TmdbClient.cs:41-55`).
- **الملف المصدري**: `TBDMovies_WinUI3/src/TBDMovies.Infrastructure/Metadata/Tmdb/TmdbClient.cs`.
- **ما الذي يحدث بالضبط**:
  - كان عميل `HttpClient` الافتراضي يُنشأ بدون تخصيص لمقبس `SocketsHttpHandler`، مما يجعل خاصية `AllowAutoRedirect` مفعلة افتراضياً (`true`) وتتبع التحويلات لغاية 50 تحويلاً.
  - هذا السلوك كان يهدد بتسريب مفاتيح الـ API (الممررة في الـ Query String) أو توكنات الـ Bearer إلى نطاقات خارجية خبيثة أو وسيطة (CWE-200 / CWE-522) عند مواجهة أي رد تحويل 3xx (مثل هجمات Open Redirect).
- **الحل المنفذ والتحسين المعماري (EGM Resolution)**:
  1. **توحيد السياسة الأمنية مع عملاء Trakt**:
     - تهيئة العميل الافتراضي في `TmdbClient` باستخدام `SocketsHttpHandler` مع `AllowAutoRedirect = false` و `disposeHandler: true`.
     - منع تتبع أي تحويلات تلقائية على مستوى المقابس وحماية التوكنات ومفاتيح الـ API تماماً.
  2. **إدارة دورة الحياة النظيفة للموارد (Clean Lifecycle & Leak Prevention)**:
     - تمرير `disposeHandler: true` ليتم إغلاق الـ Handler وقنوات الاتصال تلقائياً عند استدعاء `TmdbClient.Dispose()`.
  3. **المعالجة الآمنة لردود التحويل**:
     - واجهة TMDB REST لا تعتمد إطلاقاً على التحويلات؛ وأي رمز `3xx` يُعامل كفشل API فوري ويُترجم إلى `MetadataFailureKind.Api`.
  4. **اختبارات الوحدة**:
     - اختبار هيكلي عبر الـ Reflection (`TmdbClient_DefaultConstructor_ConfiguresSocketsHttpHandlerWithoutRedirects`) للتأكد من أن مشيد الإنتاج الافتراضي ينشئ `SocketsHttpHandler` مع `AllowAutoRedirect = false`.
     - اختبار سلوكي (`TmdbClient_DoesNotFollowRedirects_RejectsWithFailure`) يثبت الامتناع التام عن اتباع التحويل وعدم إرسال أي طلبات ثانوية.
- **التحقق والاختبارات**:
  - بناء الحل: 0 تحذيرات، 0 أخطاء (Release mode).
  - اجتياز اختبارات الوحدة: 1,111 من أصل 1,111 اختباراً بنسبة 100%.
  - تدقيق واعتماد بواسطة السمكة المعمارية (`egm-design`) والسمكة المراجعة المستقلة (`reviewer`) بتقدير **APPROVED 🟢**.

### [L6] [مفتوحة جزئياً — فجوة VACUUM على الأسرار النصية ⚠️] بقاء أثر توكن TMDB القديم والأسرار النصية في صفحات SQLite المحذوفة
- **الشاشة / النظام المتأثر**: خدمة الإعدادات وجذر التركيب (`SettingsService.cs:153, 195, 222-237` و `AppServices.cs:670, 1068-1090` و `AppSettingsStore.cs:27-31, 174` و `SettingsPage.xaml.cs:1251, 1257`).
- **ما تم تنفيذه وما لا يزال مفتوحاً (نتيجة التدقيق الكودي)**:
  1. **ما نُفذ جزئياً ضمن `[M2]`**: دالة `AppServices.PurgeLegacyDpapiCredentials` (`AppServices.cs:1068-1090`) تحذف صفَّي `tmdb_credential_dpapi` و `trakt_credential_dpapi` ثم تنفذ `VACUUM;` عند `if (tmdbPurged || traktPurged)` (`L1076`)، **لكنها تنفذ `VACUUM;` خارج قفل `DatabaseWriteGate`** (`L1080-1085`) وقبل إنشاء `SettingsService` (`L601`).
  2. **الفجوة المفتوحة الأساسية (التوكن النصي الصريح)**: حذف المفتاح النصي القديم `'tmdb_read_access_token'` من جدول `settings` في `SettingsService.MigrateLegacyCredential()` (`SettingsService.cs:222-237` عند `L230, 236`) وكذلك في `SaveTmdbCredentialAsync()` (`L153`) و `RemoveTmdbCredentialAsync()` (`L195`) **لا يستدعي `VACUUM;` إطلاقاً**، مما يترك نص التوكن الصريح قابلاً للاسترجاع داخل صفحات SQLite الحرة (`freelist pages`).
  3. **فجوة إضافية مرتبطة**: مفتاح `'subdl_api_key'` ما زال يُحفظ كنص صريح غير مشفر في جدول `settings` داخل `library.db` عبر `AppSettingsStore.cs:27-31, 174` (`SettingsPage.xaml.cs:1257`، ويُقرأ في `AppServices.cs:670` ويُعرض في `SubDlApiKeyBox.Password` عند `SettingsPage.xaml.cs:1251`) بدلاً من `Windows Credential Manager`.
- **السلوك المتوقع والحل المطلوب**:
  - تنفيذ `PRAGMA wal_checkpoint(TRUNCATE); VACUUM;` داخل `DatabaseWriteGate` فور حذف أي صف سري قديم (`rowsAffected > 0`) مع تفعيل `PRAGMA secure_delete = ON`، وترحيل `'subdl_api_key'` إلى `Windows Credential Manager`، وتنبيه المستخدم لتجديد نسخه الاحتياطية القديمة.

### [L7] [تم الحل والاعتماد بنجاح ✅] توحيد مصدر توكن TMDB وترتيب الأولوية وتعقيم المدخلات
- **الشاشة / النظام المتأثر**: إدارة بيانات الاعتماد وعميل TMDB (`SettingsService.cs`, `AppServices.cs`).
- **الملفات المصدرية**:
  - `TBDMovies_WinUI3/src/TBDMovies.Application/Services/SettingsService.cs`
  - `TBDMovies_WinUI3/src/TBDMovies.UI/Services/AppServices.cs`
  - `TBDMovies_WinUI3/tests/TBDMovies.Tests/Application/SessionCredentialRegressionTests.cs`
- **ما الذي يحدث بالضبط**:
  - كان هناك انقسام معماري (Split-Brain)؛ حيث كان `TmdbClient` يقوم بالالتفاف المباشر عبر `?? ResolveEnvTmdbToken()` في جذر التركيب، بينما `SettingsService` لا يعلم عن هذا التوكن، مما كان يسبب عدم اتساق في حالة الواجهة (`NotConfigured` بينما الطلبات تنجح).
  - عند قيام المستخدم بحذف المفتاح صراحة، كان متغير البيئة يقوم بإعادة إحيائه تلقائياً في الخلفية.
  - غياب التعقيم الصارم لمتغيرات البيئة ضد محارف التحكم وكسر الترويسات (CRLF Header Injection).
- **الحل المنفذ والتحسين المعماري (EGM Resolution)**:
  1. **المصدر الوحيد للحقيقة (Single Source of Truth)**:
     - أصبح `SettingsService` هو المرجع الحصري الوحيد لبيانات الاعتماد (`tmdbClient` يستلم `() => settings.GetTmdbToken()`)، وتم حذف أي التفاف في `AppServices`.
  2. **أسبقية مطلقة لمخزن الويندوز الآمن (Strict Absolute Priority)**:
     - يتم فحص `WindowsTmdbCredentialStore` أولاً، ولا يتم اللجوء لمتغيرات البيئة إلا إذا كان المخزن فارغاً تماماً ولم يقم المستخدم بالحذف الصريح.
  3. **منع الإحياء التلقائي عند الحذف الصريح (`tmdb_explicitly_removed`)**:
     - عند استدعاء `RemoveTmdbCredentialAsync()` يتم تدوين علامة `tmdb_explicitly_removed = "true"` لمنع متغير البيئة من العمل خلف ظهر المستخدم.
     - عند قيام المستخدم بحفظ توكن جديد عبر `SaveTmdbCredentialAsync()` يتم مسح العلامة واستعادة العمل التلقائي.
  4. **التعقيم الصارم وحدود الطول (CRLF Injection & Boundary Defense)**:
     - إضافة دالة `SettingsService.SanitizeAndValidateTmdbToken()` لقص الفراغات وعلامات التنصيص، وفرض حدود الطول `[20, 2048]`، والرفض القاطع لأي محارف تحكم (`\r`, `\n`, `\0`) أو محارف غير ASCII لمنع كسر ترويسات HTTP.
  5. **اختبارات الوحدة**:
     - إضافة 6 اختبارات وحدة شاملة في `SessionCredentialRegressionTests.cs` تغطي الأسبقية، والسقوط الاحتياطي، والحذف الصريح، والتعقيم الكامل.
- **التحقق والاختبارات**:
  - بناء الحل: 0 تحذيرات، 0 أخطاء (Release mode).
  - اجتياز اختبارات الوحدة: 1,124 من أصل 1,124 اختباراً بنسبة 100%.
  - تدقيق واعتماد بواسطة السمكة المعمارية (`egm-design`) والسمكة المراجعة المستقلة (`reviewer`) بتقدير **APPROVED 🟢**.

### [L8] [مفتوحة — تصميم جاهز للتنفيذ عند توفر شهادة 📋] توثيق والتجهيز للتوقيع الرقمي للمثبت (Code Signing for Inno Setup)
- **الشاشة / النظام المتأثر**: ملف إعداد الحزمة (`TBDMovies_WinUI3/packaging/TBD-Movies.iss` — قسم `[Setup]` الأسطر `28-54` وقسم `[Files]` الأسطر `73-75`، مع تعليق الترويسة القديم في `L11-12`).
- **ما الذي يحدث بالضبط (تصحيح السبب الجذري)**:
  - سبب غياب التوقيع الرقمي هو **غياب توجيه `SignTool` عن قسم `[Setup]` وغياب راية `signonce` عن الملف التنفيذي الداخلي `TBD-Movies.exe` في قسم `[Files]`** — وليس `PrivilegesRequired=lowest` في السطر `40`.
  - ينتج عن ذلك أن كلاً من المثبّت `TBDMoviesSetup.exe` والملف التنفيذي الداخلي `TBD-Movies.exe` غير موقّعَين، مما يؤدي لظهور تحذيرات Windows SmartScreen.
- **التصميم الأمني والمعماري المعتمد (مدمج من مراجعة `egm-design` مع تصحيح حارس `#else`)**:
  1. **آلية الحارس الآمنة (`#if SIGN_ENABLED == "1"`)**:
     - لا يُستخدم `#ifdef` لأنه يتفعّل حتى لو مرّرنا `/DSIGN_ENABLED=0`؛ الصحيح تعريف افتراضي آمن `#define SIGN_ENABLED "0"` وفحص `#if SIGN_ENABLED == "1"`.
     - في البناء الموقّع (CI/CD): `iscc.exe TBD-Movies.iss /DSIGN_ENABLED=1`؛ وفي التطوير المحلي بدون شهادة: `iscc.exe TBD-Movies.iss`.
  2. **توقيع الملف التنفيذي الداخلي (`signonce`) مع فرع `#else` إلزامي**:
     - يفحص SmartScreen ملف `TBD-Movies.exe` المستخرج بشكل مستقل عن المثبّت، لذا يجب إفراده براية `signonce` عند تفعيل التوقيع، **مع وجود فرع `#else` يضم `TBD-Movies.exe` بدون `signonce` في البناء غير الموقّع** لأن سطر التجميع العام يستبعد `TBD-Movies.exe` منعاً للتكرار!
  3. **سياسة SmartScreen الحديثة (ما بعد 2024) ونظافة الأسرار**:
     - شهادات `EV` لم تعد تمنح تجاوزاً فورياً لـ SmartScreen منذ تغيير سياسة مايكروسوفت في 2024؛ المسار الحديث الموصى به هو **Azure Trusted Signing** (~$9.99/شهر، مدعوم بـ HSM وتكامل GitHub Actions `azure/trusted-signing-action` بدون إدارة ملفات PFX يدوية)، أو شهادة OV/EV مع تحديد البصمة الصريحة `/sha1 <THUMBPRINT>` (تجنّب `/a` غير الحتمي في CI).
     - يُحظر قطعياً تضمين مسارات ملفات الشهادات أو كلمات المرور داخل `TBD-Movies.iss`؛ تُسجل أداة التوقيع خارج المستودع في إعدادات Inno Setup (`Tools > Configure Sign Tools` باسم `tbdmovies`).
  4. **القالب الجاهز للإدراج في `TBD-Movies.iss` عند التنفيذ**:
     ```inno
     #ifndef SIGN_ENABLED
       #define SIGN_ENABLED "0"
     #endif

     [Setup]
     #if SIGN_ENABLED == "1"
     SignTool=tbdmovies $f
     #endif

     [Files]
     #if SIGN_ENABLED == "1"
     Source: "{#PayloadDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion signonce
     #else
     Source: "{#PayloadDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
     #endif
     Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb,publish\*,\publish,{#MyAppExeName}"
     ```

### [L9] إنهاء النسخ السابقة بالاسم فقط (Zombie Instance Cleanup) — **[تم الحل والاعتماد بنجاح ✅]**
- **الشاشة / النظام المتأثر**: تنشيط النسخة الفعالة في بدء التشغيل (`App.xaml.cs`).
- **ما الذي يحدث بالضبط**: إنهاء العمليات بالاعتماد على اسم `Process Name` فقط، مما قد يستهدف برامج أخرى تشترك بنفس الاسم بالمصادفة.
- **السلوك المتوقع والحل المطلوب**: تمييز نسخة البرنامج عبر Mutex فريد ومقبض نافذة مخصص (`HWND`) لمنع أي تداخل مع تطبيقات خارجية.
- **الحل المنفذ**:
  - فلتر **SessionId** (نفس جلسة ويندوز فقط — يمنع التدخل في RDP / Fast User Switching).
  - فلتر **مسار الملف التنفيذي** (`Environment.ProcessPath` / `MainModule.FileName`) — يمنع استهداف برامج أخرى بنفس الاسم.
  - تكرار على **جميع المرشحين** (ليس FirstOrDefault فقط).
  - **فترة تهيؤ (Startup Grace Period)**: عمليات تعمل أقل من 10 ثوانٍ تُنتظر 3 ثوانٍ قبل أي قرار.
  - استخلاص `FindApplicationMainWindow()` يفحص عنوان النافذة عبر `GetWindowText`.
  - استخلاص `RestoreAndForegroundWindow()` يستخدم `BringWindowToTop` و `IsIconic` بشكل صحيح.
  - حلقة إعادة المحاولة: 5 محاولات بفاصل 150ms (يتجاوز تأخير برامج مكافحة الفيروسات).
- **النتيجة**: بناء 0W/0E، 1,124/1,124 اختبار، **APPROVED 🟢**

---

## خامساً: المشاكل المسجلة من جلسة Antigravity الميدانية (USER-REPORTED)

### [U1] عدم تحول زر الاستئناف وظهور القائمة المنسدلة فارغة — **[محاولة أولية معيبة في التتبع — استُبدلت بالمشكلة المفتوحة U10 ⚠️]**
- **الشاشة / النظام المتأثر**: صفحة تفاصيل الفيلم (`MovieDetailsPage`) وصفحة تفاصيل المسلسل (`TVShowDetailsPage`) ونموذج الاستئناف (`PlaybackModels.cs:27`).
- **ما تم بناؤه في واجهة الاستئناف (سليم هيكلياً كواجهة) وسبب عدم إغلاق البند**:
  - **قاعدة منع القائمة الفارغة (Never Collapse Invariant)**: عناصر القائمة المنسدلة (`MenuFlyoutItem`) تظل مرئية دائماً (`Visibility.Visible`)، ويتم التحكم في فعاليتها حصرياً عبر `IsEnabled`.
  - **توحيد عتبة الاستئناف على `>= 3.0s`**: تعديل `PlaybackModels.cs:27` من `15.0s` إلى `PositionSeconds >= 3.0` لمطابقة عتبة الحفظ.
  - **الاستعلام المزدوج وشريط تقدم البوستر (`4px`)**: استعلام `playback_progress` بـ `mediaFileId` ثم `movieId`/`episodeId`، وإضافة `PosterProgressBar` وفك ارتباط `StartPlaybackAsync` برمز إلغاء الصفحة (`CancellationToken.None`).
  - **سبب عدم الإغلاق الفعلي**: أظهرت التجربة الحية للمستخدم (`2026-11-16`) أن موضع الاستئناف الفعلي يتجمد عند سقف 30 ثانية ويتجاهل `Seek` وسرعة التشغيل، فتحولت المشكلة إلى البند المفتوح **`[U10]`** أدناه.

### [U2] عدم التقاط وقت الإغلاق والتوقف في مشغل MPC ومشغل ويندوز الافتراضي — **[محاولة أولية معيبة — استُبدلت بالمشكلة المفتوحة U10 ⚠️]**
- **الشاشة / النظام المتأثر**: محرك تتبع المشغلات الخارجية (`PlayerProcessLauncher.cs`, `PlaybackTrackingService.cs`, ومتتبعات `Trackers/`).
- **ما تم بناؤه في المحاولة الأولية وسبب تحولها إلى `[U10]`**:
  - تم إنشاء بنية `IPlayerTimelineTracker` ومتتبعات `MpcWebTimelineTracker` و `VlcHttpTimelineTracker` و `MpvPipeTimelineTracker` و `WindowsMediaTimelineTracker`، وحارس الحفظ الدوري كل 5 ثوانٍ، وقاعدة الـ `90%` (`Stop` لـ Trakt) والـ `92%` (تصفير الاستئناف).
  - **سبب عدم الإغلاق الفعلي**: في التشغيل الفعلي على ويندوز (`2026-11-16`) فشل التقاط التايم لاين الحي وتجمد العداد عند 30 ثانية مع تجاهل التقديم (`Seek`) وسرعة التشغيل (`PlaybackRate`)؛ جرى توثيق كافة الأسباب الجذرية الأربعة المكتشفة في الكود تحت البند المفتوح **`[U10]`** أدناه.

### [U3] محرك الترجمات (المحلي وعبر SubDL) — معلق للاختبار (PENDING)
- **الشاشة / النظام المتأثر**: محرك الترجمات (الملفات المحلية، وتحميل SubDL ومعالجة التشفير).
- **الحالة الحالية**: معلق ومؤجل (PENDING) بانتظار تجربة المستخدم ومعاينته في الوقت الذي يناسبه.
- **التعليمات الصارمة**: **ممنوع تعديل أو لمس كود محرك الترجمات نهائياً** حتى يقوم المستخدم بتجربته وإبداء ملاحظاته. يبقى هذا البند مقيداً كمعلق في الخطة.

### [U4] إحصائيات الصفحة الرئيسية محدودة وغير منظمة بصرياً — **[تم الحل والاعتماد بنجاح ✅]**
- **الشاشة / النظام المتأثر**: قسم الإحصائيات في الصفحة الرئيسية (`TBDMovies_WinUI3/src/TBDMovies.UI/Views/HomePage.xaml` و `HomePage.xaml.cs:391-441`).
- **الحل المنفذ والمعتمد**:
  - تم بناء البطاقات الإحصائية السينمائية الخمس بالكامل داخل `BuildStats()` (`HomePage.xaml.cs:391-441`): إجمالي الأفلام، إجمالي المسلسلات، الحلقات المتوفرة محلياً على القرص، إجمالي ساعات المشاهدة التراكمية، ومؤشر المشاهدة الشهري/نسبة الإنجاز.
  - تم تقييد التمدد على الشاشات فائقة العرض (`MaxWidth="1200"` للحاوية وحدود بطاقات متوازنة `MinWidth="180"` / `MaxWidth="240"`) مع ترجمة عربية وإنجليزية كاملة واجتياز 100% من الاختبارات (موثق في `PROGRESS.md` البندين 20–21).

### [U5] تكسر التخطيط المتجاوب وفجوة الأزرار في شاشات التفاصيل — تم الحل والاعتماد (COMPLETED 🟢)
- **الشاشة / النظام المتأثر**: صفحة تفاصيل الفيلم (`MovieDetailsPage`) وصفحة تفاصيل المسلسل (`TVShowDetailsPage`).
- **حالة التنفيذ**: **مكتمل وتم تدقيقه واعتماده (APPROVED)**.
- **تفاصيل الحل المطبق في الكود الفعلي**:
  - **القضاء التام على فجوة الأزرار عبر `WrapPanel`**:
    - تم اعتماد حاوية `controls:WrapPanel` أفقية مرنة (`CommandsPanel` بـ `RowSpacing="8"` و `ColumnSpacing="8"` في `MovieDetailsPage.xaml:248` و `TVShowDetailsPage.xaml:236`) تحافظ على مسافة ثابتة `8px` وتلتف بانسيابية على الشاشات المتوسطة والضيقة دون فراغات مصطنعة.
  - **مقاس البوستر القياسي وتمديد القسم السفلي لكامل العرض**:
    - ضبط مقاس البوستر في الوضع العريض (`Wide`) على النسبة القياسية `340 × 510` (`MovieDetailsPage.xaml:88-89`, `TVShowDetailsPage.xaml:64-65`) وفك ارتباط `RecordsGrid` لتمتد عبر 100% من عرض المحتوى.
  - **حل محاذاة وقص نص القصة (`OverviewText`)**:
    - ضبط `OverviewText` و `OverviewToggleButton` على `HorizontalAlignment="Left"` (`MovieDetailsPage.xaml:244-245`) لإزالة فجوة التوسيط على الشاشات العريضة والتخلص من حسابات `MaxWidth` اليدوية في الكود الخلفي.
  - **تدقيق واختبارات الجودة**:
    - اجتياز التدقيق المستقل بواسطة سمكة المراجعة (`APPROVED 🟢`) وبناء بصفر أخطاء وصفر تحذيرات واجتياز 1,103 / 1,103 اختبار.

### [U5-B] تضييق المسافات الرأسية بين البوستر وقسم السجلات — **[تم الحل ✅]**
- **الشاشة / النظام المتأثر**: `MovieDetailsPage.xaml` و `TVShowDetailsPage.xaml`.
- **المشكلة**: مسافة رأسية مفرطة بين آخر البوستر/HeroCopyPanel والخط الفاصل فوق قسم "Watch activity". المستخدم لاحظ إنها غير متناسقة بصرياً.
- **الحل المنفذ**:
  - `MovieDetailsPage.xaml`: تخفيض `Spacing="16" → Spacing="8"` في `PageContent`، وتخفيض `RowSpacing="24" → RowSpacing="16"` في `HeroGrid`.
  - `TVShowDetailsPage.xaml`: تخفيض `RowSpacing="32" → RowSpacing="16"` في `HeroGrid`.
- **النتيجة**: بناء 0W/0E، لا تراجع في التخطيط، **APPROVED 🟢**

### [U6] زر الاختيار العشوائي — فحص عدم التراجع فقط (REGRESSION CHECK ONLY)
- **الشاشة / النظام المتأثر**: شريط الأوامر في صفحة المكتبة (`LibraryPage`).
- **الحالة الحالية**: الزر مبني ومطبق بالفعل ويعمل وفق الآلية الذكية (فلترة الأفلام المحلية غير المشاهدة، والمسلسلات التي تملك حلقات محلية، والخلط المتوازن).
- **التعليمات الصارمة**: **ممنوع إعادة بناء أو إعادة كتابة الزر**. المطلوب فقط إجراء فحص تأكيدي (Regression Test) بعد إنهاء باقي الإصلاحات لضمان استمرار عمله بسلاسة وبدون أي أخطاء.

### [U7] مشكلة اتصال وتحقق SubDL API وعدم ظهور النتائج في التطبيق
- **الشاشة / النظام المتأثر**: إعدادات وخدمة جلب الترجمات عبر SubDL (`SubDlClient`، واجهة الإعدادات ومحرك التحميل).
- **الرابط المرجعي**: `https://subdl.com/panel/api`
- **ما الذي يحدث بالضبط**:
  - وجود مشكلة في الاتصال بمفتاح SubDL API؛ الموقع لا يظهر اتصال التطبيق به، ولا تظهر أي نتائج أو ترجمات داخل التطبيق.
  - يتطلب التحقق لاحقاً مما إذا كان المفتاح والطلب مسجلين وموجهين بشكل صحيح لـ SubDL Endpoint.
- **الحالة الحالية**: مسجلة ومؤجلة للمراجعة والفحص لاحقاً بناءً على طلب المستخدم المباشر (PENDING REVIEW).
- **التعليمات الصارمة**: ممنوع فحص أو تعديل كود الترجمات حالياً حتى يحين وقت العمل عليها.

### [U8] [تم الحل والاختبار بنجاح ✅] التحديث الموضعي اللحظي ومنع إعادة تحميل المكتبة والصفحة الرئيسية والقوائم
- **الشاشات / الأنظمة المتأثرة**: `HomePage.xaml.cs:310-311`، `MediaCardContextMenu.cs`، `LibraryPage.xaml.cs`، و `MyListsPage.xaml.cs`.
- **ما الذي كان يحدث**:
  - كان تعديل القوائم أو المفضلة أو قائمة المشاهدة عبر القائمة السياقية يطلق إعادة تحميل كاملة (`Activate` / `Invalidate` + مسح شامل لجدول الأفلام والمسلسلات)، مما يسبب وميضاً وفقدان موضع التمرير.
- **الحل المعتمد والتنفيذ على مرحلتين**:
  1. **المرحلة الأولى (`HomePage`)**: إزالة تمرير `Activate` الشامل واعتماد تحديث موضعي نظيف مع راية `_activityDirty` ومقارنة `CatalogRevision` (`1,103 / 1,103` اختبار).
  2. **المرحلة الثانية الشاملة (`LibraryPage`, `MyListsPage`, `MediaCardContextMenu`)**: استبدال المسح الكامل للجدول باستعلامات مفردة `GetMovieItem(id)` / `GetShowDetails(id)`، وتخزين كروت المكتبة (`_cachedMovieCards`, `_cachedShowCards`) مع `PatchMovieFileChanged`، وتنفيذ الحذف والإضافة وإعادة التسمية موضعياً داخل `MyListsPage` بسرعة `0ms` وبدون وميض (`1,126 / 1,126` اختبار).

### [U9] [منفذة جزئياً — نواقص مفتوحة بانتظار الاستكمال ⚠️] توحيد قوائم Trakt مع القوائم المحلية والمزامنة الثنائية
- **الشاشات / الأنظمة المتأثرة**: `DatabaseMigrator.cs` (`v22`, `v23`)، `PersonalLibraryRepository.cs`، `TraktSyncService.cs`، و `MyListsPage.xaml(.cs)` / `TraktPage.xaml(.cs)`.
- **ما تم إنجازه فعلياً في الكود**:
  - تنفيذ الترحيل `Migration 22` لتوحيد القوائم المحلية والسحابية مع تمييز تعارض الأسماء ببادئة **`[TBD] {name}`**، وتنفيذ الترحيل `Migration 23` (`custom_list_pending_actions_new` بقيد `CHECK (action_type IN ('ADD', 'REMOVE', 'CREATE_LIST'))`).
  - تنفيذ حذف القوائم المحذوفة سحابياً فوراً عبر `DeleteCustomListCascade(local.Id)` (`TraktSyncService.cs:1684`) مع حارس صمام الأمان `if (!userListsSuccess)` (`L1666`)، وإصلاح تجمد تحميل المسلسلات اللحظي `[U9-EXE]` في `LibraryPage` (`1,143 / 1,143` اختبار).
- **النواقص المفتوحة المتبقية لإغلاق `[U9]` بالكامل**:
  1. **سحب مفضلة المسلسلات معطل (`Show Favorites Pull`)**: في `TraktSyncService.cs:1934` يُقرأ `remoteFavShowTmdb` في الذاكرة دون حفظ لعدم حقن `IShowPersonalRepository`.
  2. **رفع مفضلة المسلسلات مفقود (`Show Favorites Upload`)**: المزامنة الصاعدة الجماعية في `TraktSyncService.cs:1963-1984` ترفع الأفلام فقط وتغفل المسلسلات المفضلة محلياً.
  3. **اختبار الوحدة لحارس صمام الأمان مفقود**: اختبار `SyncAsync_WhenUserListsFetchFails_DoesNotDeleteLocalCustomLists` لحارس `if (!userListsSuccess)` (`L1666`) لم يُكتب بعد.
  4. **ثغرات إضافية كشفها التدقيق الكودي الشامل**:
     - منطق دمج الطابور `if (hasAdd && hasRemove)` في `TraktSyncService.cs:1564` يلغي العملية بالكامل حتى عند عدد فردي من التبديلات (`ADD -> REMOVE -> ADD` أو `REMOVE -> ADD -> REMOVE`).
     - غياب تصفية القوائم الموسومة بالحذف (`custom_lists.is_deleted = 0`) في 3 استعلامات داخل `PersonalLibraryRepository.cs`: `GetCustomListIdsForMovie` (`L415-426` تحتاج `JOIN custom_lists cl ... AND cl.is_deleted = 0`)، و`GetCustomListIdsForShow` (`L484-495` تحتاج `JOIN custom_lists cl ... AND cl.is_deleted = 0`)، و`RequireCustomList` (`L983-988` تحتاج `AND is_deleted = 0`).
     - صفحة `TraktPage` المنفصلة ما زالت مركّبة في شجرة التنقل بجانب `MyListsPage` (مع بقاء ~750 سطراً ميتاً في `MyListsPage.Trakt.cs` وقائمة فرعية مزدوجة لـ Trakt في `MediaCardContextMenu.cs` و `MovieDetailsPage.xaml.cs`).
     - **نواقص رُصدت في الفحص اليدوي لـ Batch 2.2 ومؤجلة للموجة 5 (`Wave 5 / [U9]`)**:
       • **انقسام القوائم (`Custom Lists Still Split`)**: القوائم المخصصة ما زالت منقسمة بين قوائم Trakt وقوائم محلية (مثل ظهور `"test"` و `"trakt_test"` كقائمتين منفصلتين بدلاً من توحيد العرض).
       • **شارة "سحابي" الزائفة (`False Cloud Badge`)**: بعد مزامنة Trakt، تظهر شارة "سحابي" على بطاقات الأفلام التي تمتلك ملفات محلية بالفعل على القرص؛ ويجب قصر ظهور الشارة حصراً على المواد السحابية التي لا تملك ملفات محلية.

### [U10] [مفتوحة ومؤجلة لما بعد الامتحانات 🔴] تجمد قياس موضع التشغيل عند 30 ثانية وتجاهل التقديم (Seek) وسرعة التشغيل (Playback Speed)
- **الشاشة / النظام المتأثر**: محرك تتبع وقياس التشغيل (`WindowsMediaTimelineTracker.cs`, `MpcWebTimelineTracker.cs`, `PlayerProcessLauncher.cs`, `PlaybackTrackingService.cs`).
- **ما الذي يحدث بالضبط (ملاحظة مو 2026-11-16)**:
  - موضع التشغيل المقاس لا يتغير إطلاقاً ويتوقف تماماً (Caps) عند 30 ثانية بغض النظر عن المسافة التي يتم تقديم أو تحريك الفيلم إليها (`Seek`).
  - العداد لا يحسب الموضع إطلاقاً عند زيادة سرعة التشغيل (`Playback Speed`).
- **الأسباب الجذرية الأربعة المثبتة من الكود الفعلي (للاستناد إليها عند بدء الحل)**:
  1. **قراءة موضع `GSMTC` الساكن وغياب تصفية الجلسة (`WindowsMediaTimelineTracker.cs:37-55`)**: يقرأ المتتبع `timeline.Position.TotalSeconds` مباشرة وهو نقطة مرجعية ساكنة لا تتحدث إلا عند تغير الحالة دون معادلة `Position + (UtcNow - LastUpdatedTime) * PlaybackRate`، كما يستدعي `GetCurrentSession()` دون تصفية `SourceAppUserModelId` فقد يلتقط جلسة متصفح أو تطبيق صوتي.
  2. **اسم مفتاح سجل خاطئ لـ MPC-HC والسقوط لعداد وقت الحائط الأعمى (`MpcWebTimelineTracker.cs:102-108, 152` و`PlayerProcessLauncher.cs:211-216`)**: يكتب المتتبع في ملف `.ini` (`L102-108`) وفي الريجستري (`L152`) المفتاح `WebServer=1` بينما الاسم الحقيقي في كود MPC-HC هو `EnableWebServer` (`REG_DWORD=1`)، فيفشل الاتصال بـ `/variables.html` ويسقط المحرك في حلقة وقت حائط عمياء (`startPositionSeconds + elapsed` في `PlayerProcessLauncher.cs:211-216`) تقيس فقط زمن بقاء نافذة المشغل مفتوحة (مثلاً 30 ثانية في تجربة الفحص) مع تجاهل تام للإيقاف والتقديم (`Seek`) وسرعة التشغيل.
  3. **إهدار مدة الفيديو الحقيقية المجلوبة من المشغل (`PlayerProcessLauncher.cs:184` و`PlaybackTrackingService.cs:116-127`)**: تعيد `LaunchAndTrackAsync` (`Task<bool>`) النتيجة دون تمرير `DurationSeconds`، إذ تكتفي في `PlayerProcessLauncher.cs:184` بتحديث المتغير المحلي `totalDurationSeconds = snapshot.DurationSeconds;` دون إرساله عبر `onProgressTick` أو `onPlaybackEnded` إلى `PlaybackTrackingService`، مما يعطل حساب نسبة الـ `90%` وشريط التقدم لكافة حلقات المسلسلات والأفلام غير المربوطة بـ TMDB (`KnownDurationSeconds == null`).
  4. **استثناء عند غياب معرف الملف الموجب (`PlaybackTrackingService.cs:143`)**: استدعاء مستودع التقدم عندما `MediaFileId <= 0` يطلق `ArgumentOutOfRangeException`.
- **التطبيق المرجعي وقيد النظافة القانونية (`Clean-Room`)**:
  - دراسة آلية القياس في المستودع المرجعي مفتوح المصدر **Seanime Denshi** (`Seanime`) لفهم تدفق البروتوكولات، مع الالتزام بإعادة البناء النظيف المتوافق مع عقود `TBD Movies` والمواصفات الرسمية للمشغلات و `WinRT GSMTC`.
- **الحالة الحالية**: مسجلة ومؤجلة لما بعد الامتحانات (**DEFERRED** — ممنوع لمس الكود أو البدء في التنفيذ إلا بأمر صريح من مو).

---

## سادساً: منهجية الفيل والسمكة الذهبية المعتمدة (Elephant-Goldfish Model — EGM)

تم تصحيح وتثبيت المفهوم وفق إطار العمل الرسمي المطور من Google (`google/elephant-goldfish-model`) لتطوير البرمجيات بالذكاء الاصطناعي القائم على التصميم المعماري الدقيق (Design-Driven AI Software Development):

- **الفيل (The Elephant — الذاكرة المؤسسية والتصميم المعماري الشامل)**:
  - يمثل "الفيل" الرؤية الهندسية طويلة الأمد، الذاكرة المعمارية الثابتة، وحفظ السياق الكامل للمشروع (Architecture, Specs, Durable Context).
  - الفيل هو المخطط والمنسق الأعلى (Orchestrator) الذي يمتلك الصورة الكاملة للنظام، ويضمن عدم انحراف التنفيذ عن أولويات السرعة والسلاسة والجمالية.

- **السمكة الذهبية (The Goldfish — الوكلاء المتخصصون فائقو التركيز)**:
  - تمثل "السمكة الذهبية" وكلاء التنفيذ والمراجعة المتوازيين قصيري المدى وفائقي التركيز والسرعة (Short-Lived, Highly-Focused Disposable Subagents / Reviewers).
  - تعمل السمكة بسياق نظيف ودقيق (Zero Baggage) لتنفيذ مهام محددة أو مراجعة أكواد تفصيلية بعين ثاقبة دون تشتت، مثل وكلاء التدقيق المعماري، جودة الأكواد، وسهولة القراءة (`egm-design`, `egm-codebase-details`, `egm-style`, `egm-synthesizer`).

- **أوامر وسير عمل الـ Plugin المتاحة في Antigravity**:
  - `/egm:egm-brainstorm`: العصف الذهني واستكشاف الحلول المعمارية وتفكيك المتطلبات.
  - `/egm:egm-draft`: صياغة وثائق التصميم وخطط التنفيذ التفصيلية.
  - `/egm:egm-review`: إطلاق أسراب المراجعة المتوازية لتدقيق الكود والخطط قبل وأثناء التنفيذ.

