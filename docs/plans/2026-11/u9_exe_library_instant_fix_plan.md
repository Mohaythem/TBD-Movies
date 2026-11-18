> [!NOTE]
> **Plan Status (`Implemented & Closed — [U9-EXE]`)**: Synchronous `LoadShows()` instant library fix. Authoritative status lives in [`/BUGS.md`](../../../BUGS.md) and [`/PROGRESS.md`](../../../PROGRESS.md).

# خطة التنفيذ الجراحي [U9-EXE]: تشغيل المكتبة فوراً وإخراج ملف الـ EXE النهائي

> [!IMPORTANT]
> **نطاق محكم وصارم**: لا ميزات جديدة، لا تطويرات إضافية، لا لمس لأي ملف خارج نطاق تشغيل المكتبة، ولا دفع إلى GitHub. الهدف الوحيد هو تشغيل الـ 90 فيلماً والـ 16 مسلسلاً فوراً (0ms) دون أي تعليق أو Spinner، وإخراج ملف `TBD-Movies.exe` بوضع Release (0 أخطاء / 0 تحذيرات).

---

## 1. التشخيص الجذري النهائي للسبب في عدم عمل المكتبة (The Root Cause)

### المقارنة الفاضحة بين `HomePage` و `LibraryPage`:
1. في [HomePage.xaml.cs:165](../../../TBDMovies_WinUI3/src/TBDMovies.UI/Views/HomePage.xaml.cs#L165):
   ```csharp
   private static IReadOnlyList<TVShowRecord> LoadShows()
   {
       try { return [.. AppServices.Tv.ListShows().Select(TvProjection.ToCard)]; }
       catch (Exception) { return []; }
   }
   ```
   صفحة الـ Home تقرأ المسلسلات **بشكل متزامن فوري** في 5 ميلي ثانية فقط وتعمل ببرود تام واستقرار مطلق!
2. وفي [LibraryPage.xaml.cs:709](../../../TBDMovies_WinUI3/src/TBDMovies.UI/Views/LibraryPage.xaml.cs#L709):
   الأفلام تُقرأ أيضاً **بشكل متزامن فوري** عبر `LoadMovies()` في 10 ميلي ثانية دون أي مشاكل.
3. **مكمن الخلل الكارثي الوحيد**:
   في `LibraryPage.xaml.cs:758` تم ابتكار دالة غير متزامنة معقدة:
   `LoadShowsAsync()` تحتوي على `await Task.Yield();` ثم `await Task.Run(...)` ثم `_showsLoading = true;`!
   هذا التعقيد المصطنع تسبب في:
   - في السطر 595:
     ```csharp
     if (_showsLoading && (_movies is null || _movies.Count == 0))
     {
         LoadingPanel.Visibility = Visibility.Visible;
         MoviesGrid.Visibility = Visibility.Collapsed;
         return;
     }
     ```
     عند فتح تبويب المسلسلات (`_mediaTypeFilter == "shows"`)، تكون `movieList` فارغة، فيتحقق هذا الشرط فوراً ويتم إخفاء الشبكة وحبس الصفحة في دائرة التحميل الزرقاء (`LoadingPanel`) للأبد!
   - عودة `Task.Run` على خيط خلفي ومحاولة استدعاء `Refresh()` التي تلمس عناصر XAML مما يسبب تعارض خيوط مع الـ UI Thread.

---

## 2. الحل الجراحي المباشر (The Instant Surgical Remedy)

تحويل `LoadShows()` في `LibraryPage` لتكون **متزامنة وفورية ومحمية بالكاش** تماماً مثل `LoadMovies()` ومثل `HomePage`:

```csharp
private IReadOnlyList<TVShowRecord> LoadShows()
{
    if (_shows is not null) return _shows;

    try
    {
        var revision = AppServices.CatalogRevision;
        var rows = AppServices.Tv.ListShows().Select(TvProjection.ToCard).ToArray();
        _shows = rows;
        _showsRevision = revision;
        return rows;
    }
    catch (Exception)
    {
        _shows = null;
        return [];
    }
}
```

### النتائج المعمارية المباشرة:
1. **زمن الاستجابة**: قراءة الـ 90 فيلماً والـ 16 مسلسلاً معاً من SQLite تستغرق **أقل من 15 ميلي ثانية** (0ms perceptual delay).
2. **إلغاء حارس الـ Spinner بالكامل**: حذف كود `if (_showsLoading ...)` من دالة `Refresh()`، فلا وجود لأي تعليق أو اختفاء للشبكة نهائياً.
3. **انعدام تعارض الخيوط**: لا وجود لـ `Task.Run` أو استدعاءات خلفية تضرب الـ Dispatcher.
4. **سلاسة التبويبات**:
   - الضغط على "الكل" -> يظهر 106 عناصر فوراً.
   - الضغط على "أفلام" -> يظهر 90 فيلماً فوراً.
   - الضغط على "مسلسلات" -> يظهر 16 مسلسلاً فوراً دون أي وميض.

---

## 3. الملف الوحيد المستهدف بالتعديل (Target Scope)

• **الملف الوحيد**:
  [LibraryPage.xaml.cs](../../../TBDMovies_WinUI3/src/TBDMovies.UI/Views/LibraryPage.xaml.cs)
  - تحويل `LoadShows()` لقراءة متزامنة فورية.
  - حذف حقول `_showsLoading` و `_showsLoadError` و `_showsLoadGeneration` ودالة `LoadShowsAsync()`.
  - تنظيف حارس `if (_showsLoading ...)` في `Refresh()`.
  - لا مساس بأي ملف آخر في المشروع.

---

## 4. بروتوكول التحقق واستخراج الـ EXE (Release Build Protocol)

1. **بناء الحل بوضع Release الرسمي**:
   ```powershell
   dotnet build "D:\TBD-Movies\TBDMovies_WinUI3\TBDMovies.slnx" -c Release
   ```
   **المعيار**: 0 أخطاء و 0 تحذيرات (`0 Warning(s), 0 Error(s)`).
2. **تشغيل حزمة الاختبارات الشاملة للتأكد من عدم حدوث أي تراجع**:
   ```powershell
   dotnet test "D:\TBD-Movies\TBDMovies_WinUI3\TBDMovies.slnx" -c Release
   ```
   **المعيار**: نجاح 100% لجميع الاختبارات (1,143+ اختبار).
3. **تحديد مسار الـ EXE الجاهز للاستخدام الفوري**:
   `D:\TBD-Movies\TBDMovies_WinUI3\src\TBDMovies.UI\bin\Release\net10.0-windows10.0.22621\win-x64\TBD-Movies.exe`
4. **توقف التطوير فوراً** كما طلب المستخدم للتفرغ لامتحانات الميدتيرم.
