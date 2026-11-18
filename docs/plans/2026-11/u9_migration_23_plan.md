> [!NOTE]
> **Plan Status (`Implemented — Schema v23`)**: Matches `DatabaseMigrator.cs:718-733` (`custom_list_pending_actions_new` with `('ADD', 'REMOVE', 'CREATE_LIST')`). Authoritative `[U9]` status lives in [`/BUGS.md`](../../../BUGS.md).

# وثيقة الخطة التنفيذية المعمارية: ترقية قاعدة البيانات إلى Migration 23 وحل مشكلة القيود على القواعد القائمة

---

## 1. تشخيص المشكلة والدافع المعماري (Root-Cause Discovery & Objective)

### المشكلة المحددة بدقة متناهية:
• **السبب الجذري**: تم تعديل سكيما جدول `custom_list_pending_actions` موضعياً داخل سكريبت Migration 22 لدعم نوع الحركة `CREATE_LIST` وقيم `NULL` لحقول `media_kind` و `tmdb_id`.
• **الأثر الميداني على القواعد القائمة**: قاعدة بيانات التطبيق الحقيقية للمستخدم (`%LOCALAPPDATA%\TBD Movies\library.db`) كانت قد تمت ترقيتها بالفعل إلى الإصدار 22 (`user_version = 22`) أثناء التشغيل السابق.
• **آلية الفشل**: محرك الهجرة `DatabaseMigrator.Migrate()` يتفحص `user_version`؛ وبما أن `currentVersion == 22 == LatestVersion`، فإنه يعتبر القاعدة مكتملة التحديث ويتجاوز تشغيل Migration 22 كلياً (مسار الـ Fast Path).
• **النتيجة الحتمية وقت التشغيل**:
  عند محاولة إنشاء قائمة جديدة، يحاول التطبيق تسجيل حركة في الطابور:
  ```sql
  INSERT INTO custom_list_pending_actions (list_id, media_kind, tmdb_id, action_type, created_at)
  VALUES ($list, NULL, NULL, 'CREATE_LIST', $now);
  ```
  فتصطدم العملية بالجدول القديم الفعلي وتفشل فوراً باستثناء:
  `SQLite Error: NOT NULL constraint failed: custom_list_pending_actions.media_kind` أو `CHECK constraint failed`.
• **الهدف من الخطة**: إضافة **Migration 23** صريحة ومستقلة، وترقية `LatestVersion = 23` لضمان سريان التعديل على قاعدة البيانات الحالية لجميع الأجهزة دون أي فقدان للبيانات.

---

## 2. مصفوفة المقارنة الهندسية للبدائل (Multi-Solution Matrix)

قبل اعتماد أسلوب التنفيذ، قمنا بمقارنة كافة البدائل الهندسية الممكنة داخل بيئة SQLite:

| وجه المقارنة | الخيار A: إعادة إنشاء الجدول القياسية (Table Recreation) [موصى به] | الخيار B: استخدام `ALTER TABLE` المباشر | الخيار C: حذف وإعادة إنشاء الجدول (Drop & Recreate) |
| :--- | :--- | :--- | :--- |
| **آلية التنفيذ** | تطبيق نمط الـ 12 خطوة المعتمد من SQLite:<br>1. إنشاء جدول مؤقت `_new` بالسكيما المرنة.<br>2. نقل كافة السجلات الحالية بأمر `INSERT INTO ... SELECT`.<br>3. إسقاط الجدول القديم `DROP TABLE`.<br>4. إعادة تسمية المؤقت `RENAME TO custom_list_pending_actions`.<br>5. إعادة بناء الفهرس `idx_pending_actions_list`. | محاولة تعديل أعمدة الجدول أو حذف قيود `NOT NULL` و `CHECK` باستخدام أوامر `ALTER TABLE`. | إسقاط الجدول القديم مباشرة `DROP TABLE custom_list_pending_actions` ثم إنشاؤه مجدداً بالسكيما الجديدة. |
| **المميزات (Pros)** | • متوافق 100% مع مواصفات SQLite الرسمية ومعايير المشروع (نفس النمط المطبق في Migrations 14, 18, 22).<br>• يضمن حفظ وسلامة كافة الحركات المعلقة الموجودة حالياً في الطابور بنسبة 100%.<br>• يضمن سلامة القيود والفهارس. | يبدو كأنه أمر واحد بسيط وسريع. | سطر كود واحد مباشر وسريع جداً. |
| **العيوب والمخاطر (Cons)** | يتطلب كتابة استعلام SQL متعدد الخطوات داخل المعاملة. | **غير ممكن تقنياً في SQLite**: محرك SQLite لا يدعم مطلقاً تعديل القيود (`DROP CONSTRAINT` أو `ALTER COLUMN DROP NOT NULL`) عبر `ALTER TABLE`؛ واستخدامه سيؤدي لانهيار الهجرة بخطأ نحوي فوري. | يؤدي إلى **فقدان كامل وفوري لكافة حركات الإضافة والحذف المعلقة في الطابور** للمستخدم إذا كان أوفلاين. |

### التوصية المبررة (The Recommended Verdict):
**الخيار A (إعادة إنشاء الجدول القياسية - Table Recreation)** هو الخيار الهندسي الوحيد الصالح والمطابق لمعايير SQLite ومبدأ الحفاظ الصارم على بيانات المستخدم دون أي فقدان.

---

## 3. تفاصيل التعديلات البرمجية الصريحة (Proposed Changes)

نطاق التعديل محصور جراحياً في **ملفين اثنين فقط** دون أي مساس بملفات أخرى:

```
TBDMovies_WinUI3/
├── src/TBDMovies.Infrastructure/Persistence/Migrations/DatabaseMigrator.cs  [MODIFY]
└── tests/TBDMovies.Tests/Infrastructure/DatabaseMigrationHardeningTests.cs  [MODIFY]
```

---

### 3.1. ترقية سكيما قاعدة البيانات ومحرك الهجرة
#### [MODIFY] `D:\TBD-Movies\TBDMovies_WinUI3\src\TBDMovies.Infrastructure\Persistence\Migrations\DatabaseMigrator.cs`

1. **ترقية رقم الإصدار الأخير** (السطر 9):
   ```csharp
   // التغيير من 22 إلى 23
   public const int LatestVersion = 23;
   ```

2. **إعادة سكريبت Migration 22 إلى أصله المنضبط** (السطور 705–713):
   إبقاء سكيما Migration 22 الأصلية الثابتة لضمان التدرج الزمني السليم للهجرات التاريخية:
   ```sql
   CREATE TABLE custom_list_pending_actions (
       id INTEGER PRIMARY KEY AUTOINCREMENT,
       list_id INTEGER NOT NULL REFERENCES custom_lists(id) ON DELETE CASCADE,
       media_kind TEXT NOT NULL CHECK(media_kind IN ('MOVIE', 'SHOW')),
       tmdb_id INTEGER NOT NULL CHECK(tmdb_id > 0),
       action_type TEXT NOT NULL CHECK(action_type IN ('ADD', 'REMOVE')),
       created_at TEXT NOT NULL
   );
   CREATE INDEX idx_pending_actions_list ON custom_list_pending_actions(list_id);
   ```

3. **إضافة Migration 23 الجديدة في مصفوفة `MigrationScripts`** (بعد Migration 22 مباشرة):
   ```csharp
   // Migration 23: Relax custom_list_pending_actions constraints to support CREATE_LIST (nullable media_kind/tmdb_id)
   @"
       CREATE TABLE custom_list_pending_actions_new (
           id INTEGER PRIMARY KEY AUTOINCREMENT,
           list_id INTEGER NOT NULL REFERENCES custom_lists(id) ON DELETE CASCADE,
           media_kind TEXT CHECK(media_kind IS NULL OR media_kind IN ('MOVIE', 'SHOW')),
           tmdb_id INTEGER CHECK(tmdb_id IS NULL OR tmdb_id > 0),
           action_type TEXT NOT NULL CHECK(action_type IN ('ADD', 'REMOVE', 'CREATE_LIST')),
           created_at TEXT NOT NULL
       );

       INSERT INTO custom_list_pending_actions_new (id, list_id, media_kind, tmdb_id, action_type, created_at)
       SELECT id, list_id, media_kind, tmdb_id, action_type, created_at
         FROM custom_list_pending_actions;

       DROP TABLE custom_list_pending_actions;
       ALTER TABLE custom_list_pending_actions_new RENAME TO custom_list_pending_actions;
       CREATE INDEX idx_pending_actions_list ON custom_list_pending_actions(list_id);
   "
   ```

4. **تحديث السطر 765 لحماية القيود الخارجية (`requiresForeignKeysOff`)**:
   تعديل السطر 765 ليشمل الفهرس `22` (الخاص بـ Migration 23) صراحة التزاماً بمعايير المشروع وقاعدة القرار الصفري (The Zero-Decision Rule):
   ```csharp
   // التغيير في السطر 765:
   var requiresForeignKeysOff = i is 4 or 7 or 8 or 21 or 22;
   ```
   *ملاحظة توضيحية*: على الرغم من أن جدول `custom_list_pending_actions` هو Leaf Table لا تُشير إليه جداول أخرى بمفاتيح أجنبية، إلا أن إدراجه احترازياً يتماشى مع نمط الهجرات 4 و 7 و 8 و 21، ويضمن عدم ترك أي لبس أو قرار للمنفذ.

5. **فحص حارس التحقق التراجعي `ValidateSchema` [تأكيد التحقق - لا يتطلب تعديل كود]** (السطر 784):
   الشرط الحالي:
   ```csharp
   includeUnifiedTraktLists: i + 1 >= 22
   ```
   يتحقق تلقائياً عند تشغيل Migration 23 لأن `i + 1 = 23 >= 22`، ويفحص وجود الجدول وفهارسه وأعمدته الخمسة دون الحاجة لأي تعديل في الكود.

---

### 3.2. إضافة اختبارات التحقق من ترقية السكيما وحفظ البيانات
#### [MODIFY] `D:\TBD-Movies\TBDMovies_WinUI3\tests\TBDMovies.Tests\Infrastructure\DatabaseMigrationHardeningTests.cs`

إضافة اختبار مخصص يتأكد بدقة متناهية من سلامة الانتقال من v22 إلى v23:
```csharp
[Fact]
public void V22_To_V23_Migration_RelaxesPendingActionsConstraints_WithoutDataLoss()
{
    // 1. إنشاء قاعدة بيانات على v22 وتغذيتها بقائمة وسجل طابور صالح
    new DatabaseMigrator(_cs).Migrate(22);
    using (var db = Open())
    {
        Exec(db, "INSERT INTO custom_lists (id, display_name, created_at) VALUES (1, 'Test List', '2026-01-01');");
        Exec(db, "INSERT INTO custom_list_pending_actions (id, list_id, media_kind, tmdb_id, action_type, created_at) VALUES (10, 1, 'MOVIE', 550, 'ADD', '2026-01-01');");

        // توكيد سلبي قبل الترقية (Negative Baseline): التأكد من أن v22 كانت ترفض CREATE_LIST مع قيم NULL
        Assert.Throws<SqliteException>(() =>
            Exec(db, "INSERT INTO custom_list_pending_actions (list_id, media_kind, tmdb_id, action_type, created_at) VALUES (1, NULL, NULL, 'CREATE_LIST', '2026-01-01');"));
    }

    // 2. تشغيل الترقية إلى v23
    new DatabaseMigrator(_cs).Migrate(23);

    // 3. التحقق من سلامة الترقية وحفظ البيانات
    using var upgraded = Open();
    Assert.Equal(23L, Scalar(upgraded, "PRAGMA user_version"));
    Assert.Equal(10L, Scalar(upgraded, "SELECT id FROM custom_list_pending_actions WHERE id = 10"));
    Assert.Equal("MOVIE", Scalar(upgraded, "SELECT media_kind FROM custom_list_pending_actions WHERE id = 10"));

    // 4. التحقق الإيجابي: قبول إدراج حركة CREATE_LIST مع قيم NULL بنجاح تام بعد الترقية
    Exec(upgraded, "INSERT INTO custom_list_pending_actions (list_id, media_kind, tmdb_id, action_type, created_at) VALUES (1, NULL, NULL, 'CREATE_LIST', '2026-01-02');");
    Assert.Equal(2L, Scalar(upgraded, "SELECT COUNT(*) FROM custom_list_pending_actions"));

    // 5. التحقق السلبي بعد الترقية: التأكد من أن قيود CHECK لا تزال تحمي من القيم غير الصالحة
    Assert.Throws<SqliteException>(() =>
        Exec(upgraded, "INSERT INTO custom_list_pending_actions (list_id, media_kind, tmdb_id, action_type, created_at) VALUES (1, 'INVALID_KIND', 550, 'ADD', '2026-01-02');"));
    Assert.Throws<SqliteException>(() =>
        Exec(upgraded, "INSERT INTO custom_list_pending_actions (list_id, media_kind, tmdb_id, action_type, created_at) VALUES (1, 'MOVIE', 550, 'INVALID_ACTION', '2026-01-02');"));
}
```

---

## 4. بروتوكول التحقق الصارم وضمان الجودة (Verification Plan)

### 4.1. البناء القياسي بنمط Release (`dotnet build`)
تنفيذ البناء الصارم لكافة مشاريع الحل البرمجي تحت تكوين `Release`:
```powershell
dotnet build "D:\TBD-Movies\TBDMovies_WinUI3\TBDMovies.slnx" -c Release
```
• **معيار القبول**: خروج البناء بكود `0` وخلوه التام من أي أخطاء أو تحذيرات (`0 Warning(s), 0 Error(s)`).

### 4.2. تشغيل حزمة الاختبارات الشاملة بنمط Release (`dotnet test`)
تشغيل كافة الاختبارات للتأكد من استقرار الأساس (1,132 اختبار) بالإضافة للاختبار الجديد:
```powershell
dotnet test "D:\TBD-Movies\TBDMovies_WinUI3\tests\TBDMovies.Tests\TBDMovies.Tests.csproj" -c Release --no-build
```
• **معيار القبول**: اجتياز **1,133 / 1,133 اختباراً بنسبة 100%**.

### 4.3. التحقق الميداني الحي على قاعدة بيانات المستخدم الحقيقية (Real-World DB Verification)
1. تشغيل سكريبت فحص مباشر على قاعدة بيانات جهازك الفعلية:
   `%LOCALAPPDATA%\TBD Movies\library.db`
   والتأكد من:
   - تحول `PRAGMA user_version` من 22 إلى 23.
   - تحول كود تعريف `custom_list_pending_actions` في `sqlite_master` إلى السكيما المرنة التي تحتوي على `CREATE_LIST` وتقبل `NULL`.
2. اختبار تجربة المستخدم الحية:
   - الضغط على زر "+ قائمة جديدة".
   - إدخال اسم قائمة محلية.
   - التأكد من إنشاء القائمة بنجاح فوري دون أي استثناء، وتسجيل حركة `CREATE_LIST` في الطابور ومزامنتها مع Trakt.

---

## 5. حدود الأمان والانضباط المعماري (Safety & Scope Discipline)

- **الملفات المستهدفة**: التعديل مقصور بنسبة 100% على ملفين فقط:
  1. `src\TBDMovies.Infrastructure\Persistence\Migrations\DatabaseMigrator.cs`
  2. `tests\TBDMovies.Tests\Infrastructure\DatabaseMigrationHardeningTests.cs`
- **الحفاظ على Git**: لا يوجد أي أمر تفريغ أو مساس بملفات أخرى، وممنوع نهائياً إجراء أي Push لـ GitHub.
- **التنفيذ الجراحي**: لا يتم لمس أو تعديل أي سطر في الكود إلا بعد الحصول على الموافقة والاعتماد الصريح لهذه الوثيقة من المستخدم.

---

## 6. [U9-متابعة] توسعة المزامنة الشاملة: ربط المفضلة وكافة مجموعات البرنامج وحذف القوائم عن بُعد

### 6.1. ربط المفضلة المدمجة بـ Trakt (`/sync/favorites`)
- **تصحيح المفهوم وتأكيد مواصفات Trakt الرسمية**:
  - يوفر Trakt APIv2 نقاط نهاية مدمجة ونقية (Native Favorites) مخصصة حصراً للأفلام والمسلسلات:
    - **استرجاع المفضلة**: `GET https://api.trakt.tv/sync/favorites` (أو بالفرز `/sync/favorites/movies` و `/sync/favorites/shows`).
    - **إضافة مادة للمفضلة**: `POST https://api.trakt.tv/sync/favorites` مع حمولة `{ "movies": [{ "ids": { "tmdb": X } }], "shows": [{ "ids": { "tmdb": Y } }] }`.
    - **إزالة مادة من المفضلة**: `POST https://api.trakt.tv/sync/favorites/remove` بنفس تركيبة الحمولة.
  - **حقيقة الـ "Likes" في Trakt**: الـ Likes في Trakt (`/users/{id}/lists/{list_id}/like`) مخصصة للمحتوى المجتمعي (قوائم المستخدمين الآخرين أو تعليقاتهم)، ولا وجود لإعجاب بمادة فنية عبر هذا المسار.
- **التكامل البرمجي مع كود TBD Movies القائم**:
  - **الأفلام**: تُمثّل المفضلة محلياً في جدول `movie_personal_state` عبر العمود `preference = 'LIKED'` (`PersonalLibraryRepository.cs:L37`).
  - **المسلسلات**: تُمثّل المفضلة محلياً في جدول `tv_show_personal_state` عبر العمود `is_favorite = 1` (`ShowPersonalLibraryRepository.cs:L56-L60`).
  - **المعمارية السحابية**:
    - إضافة دوال `AddFavoriteAsync` و `RemoveFavoriteAsync` إلى عميل `TraktDataClient`.
    - في واجهات التفاصيل (`MovieDetailsPage.xaml.cs:L919` و `TVShowDetailsPage.xaml.cs:L408`): عند تبديل حالة المفضلة، يتم تحديث SQLite محلياً فوراً (0ms)، ثم إرسال طلب الشبكة في الخلفية، أو تسجيل الحركة في طابور العمليات غير المتصلة إذا تعذر الاتصال.
    - أثناء دورة المزامنة: مزامنة ثنائية الاتجاه تقرأ `/sync/favorites` وتُحدّث حالتي `movie_personal_state` و `tv_show_personal_state` محلياً.

---

### 6.2. المصفوفة الهندسية الشاملة لربط كافة مجموعات وقوائم التطبيق بـ Trakt

فحصنا الكود الفعلي لجميع القوائم والمجموعات في TBD Movies ومقابلها في Trakt API:

| المجموعة المحلية في TBD Movies | الملف المسؤول في الكود | نقطة النهاية المقابلة في Trakt API | نمط المزامنة الموصى به | التحليل والتعليل المعماري (Pros & Cons) |
| :--- | :--- | :--- | :--- | :--- |
| **المفضلة (Favorites)** | `movie_personal_state.preference`<br>`tv_show_personal_state.is_favorite`<br>(`PersonalLibraryRepository.cs:L37`, `ShowPersonalLibraryRepository.cs:L56`) | `sync/favorites`<br>`sync/favorites/remove` | **اتجاهين (Two-Way)** | • **الميزة**: مطابقة 1:1 مع المفضلة السحابية للمستخدم على Trakt.<br>• **الآلية**: قراءة وتحديث متبادل مع طابور العمليات غير المتصلة. |
| **المشاهدة لاحقاً (Watch Later / Watchlist)** | `movie_personal_state.watchlist_added_at`<br>`tv_show_personal_state.watch_later_added_at` | `sync/watchlist`<br>`sync/watchlist/remove` | **اتجاهين (Two-Way)** | • **الميزة**: موجودة بالفعل جزئياً في `TraktDataClient.Watchlist.cs`.<br>• **الآلية**: استكمال تفريغ الطابور وسحب التحديثات السحابية. |
| **سجل المشاهدة (Watch History)** | `watch_events` table<br>(`WatchHistoryRepository.cs`) | `sync/history`<br>`sync/history/remove` | **اتجاهين (Two-Way)** | • **الميزة**: مدعومة في `TraktDataClient.History.cs` ومكتملة المعمارية.<br>• **الآلية**: تسجيل المشاهدات محلياً وسحابياً مع تفريغ الأحداث. |
| **المكتبة المحلية (Local Media Library)** | `movies`, `tv_shows`, `media_files`<br>(`MovieRepository.cs`, `MediaFileRepository.cs`) | `sync/collection`<br>`sync/collection/remove` | **اتجاه واحد صاعد (Local -> Trakt Collection)** | • **التحليل**: تمثل في Trakt الوسائط المملوكة رقمياً/فيزيائياً للمستخدم (Collection).<br>• **لماذا اتجاه واحد صاعد؟** لأن التطبيق لا يمكنه تنزيل ملفات فيديو من السيرفر لمواد غير موجودة على القرص، ولكن رفع ما يملكه المستخدم محلياً إلى Trakt Collection يمنحه سجلاً سحابياً دقيقاً لما يمتلكه. |
| **متابعة المشاهدة (Continue Watching)** | `playback_progress` table<br>(`PlaybackProgressRepository.cs`) | `sync/playback`<br>`scrobble/pause` | **اتجاهين (Two-Way)** | • **الميزة**: قراءة نقاط التوقف واستئناف المشاهدة عبر الأجهزة.<br>• **الآلية**: مزامنة النسبة المئوية `progress` وموضع التوقف بالثواني. |
| **القوائم المخصصة (Custom Lists)** | `custom_lists`, `custom_list_members`<br>(`PersonalLibraryRepository.cs`) | `users/me/lists/{id}`<br>`users/me/lists/{id}/items` | **اتجاهين (Two-Way)** | • **الميزة**: جوهر مهمة [U9]؛ دمج كامل في شريط التبويبات الموحد مع معالجة تعارض الأسماء بـ `[TBD]`. |
| **التقييمات (Ratings)** | `movies.rating`, `tv_shows.rating`<br>(`DatabaseMigrator.cs:L30, L334`) | `sync/ratings`<br>`sync/ratings/remove` | **غير مدعوم محلياً حالياً (No Local Counterpart)** | • **حقيقة صريحة**: حقل `rating` الموجود محلياً في جداول `movies` و `tv_shows` هو تقييم المجتمع العام المستورد من TMDB (Community Metadata)، وليس تقييماً شخصياً للمستخدم.<br>• **المتطلب في حال الرغبة بتفعيله**: يتطلب إنشاء جدول محلي جديد `user_ratings` وإضافة واجهة لتقييم النجوم (1-10) في صفحات التفاصيل، ثم ربطها بـ `/sync/ratings`. |

---

### 6.3. معمارية كشف وحذف القوائم عن بُعد (Remote List Deletion Detection)

#### 1. توصيف المشكلة:
في الكود الحالي لـ `TraktSyncService.ReconcileCustomListsAsync`:
- يتم جلب القوائم الحالية من السيرفر `remoteLists = GetLists(accountId)`.
- يتم عمل `Upsert` لكل قائمة قادمة من السيرفر.
- **الثغرة**: إذا قام المستخدم بحذف قائمة من موقع Trakt أو تطبيقه على الهاتف، فإن تلك القائمة تختفي من `remoteLists`، لكن محرك المزامنة لا يكتشف اختفاءها، فتظل القائمة مخزنة محلياً في `custom_lists` بـ `trakt_list_id` القديم ولا تُحذف أبداً.

#### 2. خوارزمية كشف القوائم المحذوفة عن بُعد (Detection Algorithm):
1. جمع كافة المعرفات السحابية الحية في مجموعة:
   ```csharp
   var remoteTraktIds = remoteLists.Select(l => l.TraktId).ToHashSet();
   ```
2. استعلام كافة القوائم المحلية المربوطة بـ Trakt وغير المحذوفة محلياً:
   ```csharp
   var localLinkedLists = _personalLibraryRepository.ListCustomLists()
       .Where(l => l.TraktListId.HasValue && !l.IsDeleted);
   ```
3. تحديد القوائم المحذوفة سحابياً:
   ```csharp
   var remotelyDeletedLists = localLinkedLists
       .Where(local => !remoteTraktIds.Contains(local.TraktListId.Value))
       .ToList();
   ```

#### 3. مصفوفة حسم تعارض الحذف السحابي مقابل التعديلات المحلية (Conflict Resolution Matrix):

| الحالة الميدانية | الإجراء المعتمد | التعليل الهندسي والنتيجة |
| :--- | :--- | :--- |
| **حالة 1: القائمة لم تُعدل محلياً**<br>(لا توجد حركات معلقة في الطابور لها) | **حذف محلي فوري (Cascade Delete)** | القائمة حُذفت برغبة المستخدم الصريحة على Trakt ولا توجد أي تعديلات جديدة عليها محلياً؛ تُحذف القائمة وأعضاؤها فوراً من SQLite للحفاظ على التطابق 1:1. |
| **حالة 2: القائمة عُدلت محلياً أثناء عدم الاتصال**<br>(توجد حركات إضافة/حذف معلقة في الطابور) | **فصل القائمة وتحويلها لمحلية آمنة (Detach to Local)** | • **المنع من فقدان البيانات**: بدلاً من مسح القائمة وضياع التنسيق الذي قام به المستخدم محلياً، يتم فك ارتباطها عن السيرفر بتصفير `trakt_list_id = NULL` و `trakt_slug = NULL`، وتسميتها ببادئة `[محلية] ` أو `[TBD] `.<br>• شطب حركات الإرسال لـ Trakt القديمة لأن القائمة لم تعد موجودة سحابياً، مع إشعار المستخدم بذلك في رأس الصفحة. |
| **حالة 3: حذف قائمة محلياً متزامناً مع حذفها سحابياً** | **إتمام الحذف وشطب الـ Tombstone** | يتم تنظيف سجل الـ Tombstone المحلي (`is_deleted = 1`) وشطب القائمة نهائياً. |

---

### 6.4. خارطة طريق التنفيذ المقترحة ومصفوفة القرار (Phasing & Execution Decision)

قبل البدء في أي كود، نعرض على المستخدم خيارات مسار التنفيذ:

| المسار المقترح | النطاق المشمول | المميزات | المخاطر والاعتبارات | التوصية |
| :--- | :--- | :--- | :--- | :--- |
| **المسار A: التنفيذ المرحلي المنفصل (Two-Stage Execution)** | **المرحلة 1**: تنفيذ Migration 23 فوراً (ملفان فقط).<br>**المرحلة 2**: تنفيذ ربط المفضلة وكشف الحذف عن بُعد بعد استقرار القاعدة. | • حل المشكلة العاجلة الحالية (خطأ إنشاء القوائم) في 5 دقائق وبأقل تغيير ممكن.<br>• التحقق من قاعدة مو الحقيقية بنسبة 100% قبل إدخال أي منطق شبكي جديد.<br>• سهولة المراجعة والعزل التام للأخطاء. | يتطلب دورة اعتماد وتنفيذ ثانية للمرحلة التالية. | **(موصى به بشدة 🟢)** |
| **المسار B: التنفيذ الشامل الموحد (All-in-One Execution)** | دمج Migration 23 + ربط المفضلة بـ Trakt + كشف الحذف عن بُعد في دفعة تنفيذية واحدة. | إنجاز كل شيء في جلسة واحدة. | • اتساع رقعة التغيير (تعديل عميل Trakt، المستودعات، شاشات التفاصيل، محرك المزامنة).<br>• تعقيد المراجعة وزيادة احتمالية تعطل القاعدة أثناء وجود خطأ في منطق المزامنة. | غير محبذ هندسياً |

---

### 6.5. الملفات المعنية في حال اعتماد تنفيذ توسعة Trakt (المرحلة 2):
1. `src\TBDMovies.Application\Trakt\ITraktDataClient.cs` (إضافة عقود Favorites).
2. `src\TBDMovies.Infrastructure\Trakt\TraktDataClient.cs` (أو فئة جزئية `TraktDataClient.Favorites.cs` لنقاط نهاية `/sync/favorites`).
3. `src\TBDMovies.Application\Trakt\TraktSyncService.cs` (إضافة خوارزمية كشف الحذف عن بُعد ومزامنة المفضلة).
4. `src\TBDMovies.Infrastructure\Persistence\Repositories\PersonalLibraryRepository.cs` (دوال فك الارتباط Detach وحذف القوائم محلياً).
5. `src\TBDMovies.UI\Views\MovieDetailsPage.xaml.cs` و `TVShowDetailsPage.xaml.cs` (ربط زر المفضلة بالمزامنة السحابية).

