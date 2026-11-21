# TBD Movies — وثيقة التسليم الموحدة وخارطة الطريق النشطة (HANDOFF)

**تاريخ التوثيق والمراجعة**: 21 نوفمبر 2026  
**الإصدار الحالي**: `v0.12.5` (`TBD-Movies.exe` / `TBDMoviesSetup.exe`)  
**الفرع الحالي**: `codex` (محدث ومرفوع بالكامل لـ GitHub Actions CI — **100% Green**)  
**بيئة العمل والتقنيات**: WinUI 3 / **C# 14** / .NET 10 (`net10.0-windows10.0.22621`) / Windows App SDK `1.7.260224002` / SQLite WAL (`library.db` — **`DatabaseMigrator.LatestVersion = 23`**) / نمط غير محزم (`win-x64` Unpackaged).  
**حالة البناء المرجعية على ويندوز**: بناء `Release` نظيف (`0 Warning(s), 0 Error(s)`).  
**خط الأساس المرجعي للاختبارات**: **`1,343 / 1,343`** حالة اختبار (اجتياز 100% دون أي فشل أو تراجع: `Failed: 0, Passed: 1,343, Skipped: 0`).  
**حالة التطوير العامة**: **الدفعتان 4.1 و 4.2 مكتملتان بنجاح (Batch 4.1 & 4.2)**. الجاهزية لبدء **الموجة 5 (Wave 5: [U9] توحيد قوائم Trakt مع القوائم المحلية)**.

---

## 🚨 التوجيهات والمراجع الدستورية الإلزامية للوكيل القادم

قبل اتخاذ أي خطوة، اقرأ والتزم حرفياً بالوثائق المرجعية المعتمدة في جذر المستودع بالترتيب التالي:

• **`/AGENTS.md`**: الدستور الأعلى الموحد (قواعد رمز الكناري، محاذاة `RTL`، `/grill-me`، مصفوفة البدائل المتعددة، حظر `git push` وأوامر Git التدميرية، انضباط الـ Subagents المتتابع، والثوابت البرمجية الملزمة في WinUI 3).
• **`/BUGS.md`**: المصدر الوحيد للحقيقة لحالة كافة المشاكل المسجلة (`H / M / P / L / U`).
• **`/PROGRESS.md`**: السجل التسلسلي النظيف للدفعات المنفذة.
• **`/EGM.md`**: خريطة العقود المعمارية الشاملة لكافة ملفات المشروع الـ `333` وجداول SQLite العشرين (`Schema v23`).
• **`/AUDIT_FINDINGS_REPORT.md`**: تقرير التدقيق الهندسي العميق الشامل (المشاكل المكتشفة `C-01..C-17`، جرد الكود الميت والتكرار، ومصفوفة الحلول البديلة لكل مشكلة).
• **`/SECURITY_AND_TRY_CATCH_AUDIT.md`**: تقرير الفحص الأمني الجنائي (`SEC-01..SEC-26`) والجرد الشامل المصنف لكافة بلوكات `try/catch` (`544` بلوك فعلي + `3` تعليقات).

> [!CAUTION]
> **تنبيه تشغيلي حرج لعزل الاختبارات على ويندوز:**
> اسم متغير البيئة الفعلي في الكود (`AppServices.cs:77`) لعزل مسار البيانات والاختبارات هو **`TBDMOVIES_TEST_DATA_ROOT`** (أحرف كبيرة متصلة **بدون أي مسافة**). إياك واستخدام الاسم الخاطئ القديم `TBD Movies_TEST_DATA_ROOT` بمسافة؛ لأنه يجعل `GetTestDataRoot()` يعيد `null` فيعمل الاختبار على قاعدة بيانات المستخدم الحية وتوكناته الحقيقية!

---

## 🛠️ ملخص الإنجازات المعمارية الأخيرة (حتى `Schema v23` و `1,143` اختباراً)

### أولاً: الترحيل `Migration 23` وإصلاح قيد طابور القوائم المخصصة (`DatabaseMigrator.cs:718-733`)
• **المشكلة**: في `v22` كان جدول `custom_list_pending_actions` يفرض `NOT NULL` على `media_kind` و `tmdb_id`، مما كان يُفشل تسجيل إجراء إنشاء قائمة مخصصة جديدة (`CREATE_LIST`) غير مرتبطة بعنصر وسائط.
• **التنفيذ الفعلي في الكود**:
  - إنشاء جدول بديل باسم **`custom_list_pending_actions_new`** يسمح بقيم `NULL` في `media_kind` و `tmdb_id` ويطبّق قيد التحقق الفعلي:
    `CHECK (action_type IN ('ADD', 'REMOVE', 'CREATE_LIST'))` *(لا يوجد `_v22_backup` ولا `'DELETE_LIST'` في هذا القيد)*.
  - نسخ الصفوف الموجودة، حذف الجدول القديم، إعادة تسمية الجدول الجديد إلى `custom_list_pending_actions`، وإعادة بناء الفهرس `idx_pending_actions_list`.

### ثانياً: المرحلة الثانية لمزامنة Trakt وتوحيد القوائم (`[U9]` — منفذة جزئياً)
• **ما تم تنفيذه فعلياً**:
  - توحيد القوائم المحلية وقوائم Trakt المخصصة في شريط واحد داخل `MyListsPage` مع تمييز تعارض الأسماء تلقائياً ببادئة **`[TBD] {name}`** وعرض شارة العناصر السحابية.
  - طابور المفضلة أوفلاين (`trakt_pending_favorite_actions`) ودعم رفع المجموعة المحلية (`/sync/collection`).
  - **حذف القوائم المحذوفة سحابياً فوراً**: عند ثبوت حذف قائمة على موقع Trakt مع نجاح الجلب (`userListsSuccess == true`)، تُحذف القائمة محلياً فوراً عبر **`DeleteCustomListCascade(local.Id)`** (`TraktSyncService.cs:1684`) — بينما تُستخدم حالة `PENDING_DELETE` (`is_deleted = 1`) حصراً للقوائم المحذوفة محلياً بانتظار رفع الحذف إلى Trakt.
  - **صمام الأمان (`Safety Valve`)**: حارس `if (!userListsSuccess) return;` في `TraktSyncService.cs:1666` لمنع مسح القوائم المحلية عند فشل الشبكة.

### ثالثاً: إصلاح تجمد صفحة المكتبة اللحظي (`[U9-EXE]` — `LibraryPage.xaml.cs`)
• استبدال `LoadShowsAsync()` المغلفة بـ `Task.Run` / `Task.Yield` باستدعاء متزامن فوري `LoadShows()` على غرار `LoadMovies()` وإزالة مصيدة `LoadingPanel` وتصفير `_movies` الذاتي عند تغير `CatalogRevision`، مما حقق عرضاً لحظياً (`< 5ms`) بدون وميض.

### رابعاً: بنية الاستئناف والمتتبعات الأولية (`[U1]` / `[U2]` ⬅ استُبدلتا بـ `[U10]`)
• تم بناء واجهة `SplitButton` غير القابلة للانهيار، وشريط تقدم البوستر (`4px`)، وتوحيد عتبة الاستئناف في `PlaybackModels.cs:27` إلى **`PositionSeconds >= 3.0`**، وبنية `IPlayerTimelineTracker` مع قاعدة الـ `90%` (`Stop` لـ Trakt) والـ `92%` (تصفير الاستئناف)؛ غير أن التجربة الحية كشفت تجمد قياس الوقت الفعلي عند 30 ثانية فانتقل مسار التتبع إلى المشكلة المفتوحة **`[U10]`**.

---

## 🔍 خط الأساس الحسابي للاختبارات وملاحظات بيئة التشغيل

• **التسلسل التاريخي الموثق للاختبارات**:
  - إصدار `v0.12.5` الأساسي: `1,029` اختباراً.
  - نهاية `[U8]` الشاملة: `1,126` اختباراً.
  - بعد اختبارات `UnifiedTraktListsTests` الأولية لـ `[U9]`: `1,131` ثم `1,132` اختباراً.
  - بعد اختبارات متتبعات `[U2]`: `1,135` اختباراً.
  - بعد `Migration 23`: `1,136` اختباراً.
  - بعد اختبارات تحصين Trakt وطابور المفضلة أوفلاين: **`1,143 / 1,143` اختباراً**.
• **أمر التحقق القياسي على ويندوز**:
```powershell
dotnet test "TBDMovies_WinUI3\TBDMovies.slnx" -c Release
```
• **ملاحظة تقنية محفوظة لمشكلة `Git Push HTTP 408` عند الحاجة مستقبلاً (بأمر المستخدم فقط)**:
  - في حال فشل رفع الحزم الكبيرة مستقبلاً بـ `RPC failed; HTTP 408`:
    `git config http.postBuffer 524288000` و `git config http.version HTTP/1.1`.

---

## 📌 سجل المهام والنواقص المفتوحة (Open & Deferred Engineering Backlog)

*(جميع البنود أدناه مؤجلة ولا يُلمس أي كود خاص بها إلا بأمر صريح ومباشر من المستخدم)*

• **أولاً — استكمال نواقص `[U9]` المفتوحة (Trakt Unified Lists & Favorites)**:
  - **سحب مفضلة المسلسلات (`Show Favorites Pull`)**: `remoteFavShowTmdb` في `TraktSyncService.cs:1934` يُقرأ في الذاكرة دون حفظ لعدم حقن `IShowPersonalRepository`.
  - **رفع مفضلة المسلسلات (`Show Favorites Upload`)**: المزامنة الصاعدة الجماعية في `TraktSyncService.cs:1963-1984` ترفع الأفلام فقط وتغفل المسلسلات.
  - **اختبار الوحدة لحارس صمام الأمان**: كتابة اختبار `SyncAsync_WhenUserListsFetchFails_DoesNotDeleteLocalCustomLists` لحارس `if (!userListsSuccess)` (`L1666`).
  - **ثغرات إضافية موثقة من التدقيق**: منطق دمج الطابور `if (hasAdd && hasRemove)` (`TraktSyncService.cs:1564`) يلغي آخر إجراء في التبديلات الفردية (`ADD -> REMOVE -> ADD`)، وغياب تصفية القوائم الموسومة بالحذف (`custom_lists.is_deleted = 0`) في `PersonalLibraryRepository.cs` داخل `GetCustomListIdsForMovie` (`L415-426` عبر `JOIN custom_lists cl ... AND cl.is_deleted = 0`) و`GetCustomListIdsForShow` (`L484-495` عبر `JOIN custom_lists cl ... AND cl.is_deleted = 0`) و`RequireCustomList` (`L983-988` عبر `AND is_deleted = 0`)، وبقاء صفحة `TraktPage` المنفصلة والأسطر الميتة في `MyListsPage.Trakt.cs`.

• **ثانياً — `[U10]` تجمد قياس موضع التشغيل عند 30 ثانية وتجاهل التقديم والسرعة (تحل محل `[U1]` / `[U2]`)**:
  - **الأسباب الجذرية الأربعة في الكود**: (أ) قراءة `GSMTC Position` الساكن في `WindowsMediaTimelineTracker.cs:37-55` بدون `(UtcNow - LastUpdatedTime) * PlaybackRate` وبدون تصفية `SourceAppUserModelId`، (ب) كتابة `WebServer=1` بدلاً من `EnableWebServer` لـ MPC-HC في `MpcWebTimelineTracker.cs:102-108, 152` والسقوط في حلقة وقت الحائط العمياء `startPositionSeconds + elapsed` في `PlayerProcessLauncher.cs:211-216` التي تجهل الإيقاف والتقديم وسرعة التشغيل، (ج) إهدار `DurationSeconds` المجلوبة من المشغل في `PlayerProcessLauncher.cs:184` (تحديث المتغير المحلي `totalDurationSeconds` دون تمريره إلى `PlaybackTrackingService.cs:116-127`)، (د) استثناء `ArgumentOutOfRangeException` عند `MediaFileId <= 0` في `PlaybackTrackingService.cs:143`.

• **ثالثاً — `[L6]` فجوة `VACUUM` على الأسرار النصية في SQLite**:
  - يعمل `VACUUM` حالياً فقط في مسار تطهير `DPAPI` (`[M2]`، وهو يعمل خارج `DatabaseWriteGate` في `AppServices.cs:1080-1085`)، بينما حذف `'tmdb_read_access_token'` النصي في `SettingsService.MigrateLegacyCredential()` (`SettingsService.cs:222-237` عند `L230, 236`) و`SaveTmdbCredentialAsync()` (`L153`) و`RemoveTmdbCredentialAsync()` (`L195`) لا يستدعي `VACUUM;` إطلاقاً؛ كما يُحفظ `'subdl_api_key'` كنص صريح في جدول `settings` عبر `AppSettingsStore.cs:27-31, 174` (`SettingsPage.xaml.cs:1257`، ويُقرأ في `AppServices.cs:670` و`SettingsPage.xaml.cs:1251`).

• **رابعاً — `[L8]` التوقيع الرقمي لمثبّت Inno Setup (`TBD-Movies.iss`)**:
  - غياب توجيه `SignTool` في `[Setup]` وراية `signonce` في `[Files]`؛ التصميم الكامل جاهز وموثق داخل `/BUGS.md` تحت `[L8]` (شاملاً فرع `#else` لحماية البناء غير الموقّع).

• **خامساً — باقي البنود المفتوحة أو المجمّدة في `/BUGS.md`**:
  - **`[M1]`**: حماية مفتاح TMDB v3 (الذي يشترط Query String على خوادم TMDB مقابل v4 JWT Bearer).
  - **`[U7]` و `[U3]`**: محرك الترجمات وواجهة SubDL API (**مجمّدان — ممنوع لمس كودهما حتى يأمر المستخدم**).
  - **`[U6]`**: فحص عدم التراجع فقط لزر الاختيار العشوائي (`RandomPickerButton`) بعد انتهاء الإصلاحات.
  - **نتائج الفحص المعماري الشامل (`C-01` إلى `C-17` والكود الميت)**: موثقة بالكامل مع مصفوفة الحلول البديلة في `/AUDIT_FINDINGS_REPORT.md` للمراجعة المتتابعة مع المستخدم.
