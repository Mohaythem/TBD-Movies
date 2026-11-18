# TBD Movies — سجل وبروتوكول المعالجة الهندسية المتدرجة (`REMEDIATION_PROCESS.md`)

> **الإصدار المستهدف:** `v0.12.5` ⬅ `v0.12.6`
> **مساحة العمل المعزولة:** `/home/antigravity/TBD-Movies-Audit` (مستودع `Git` محلي معزول بالكامل بدون `remote`؛ مستودع `/home/antigravity/Mohaythem/TBD-Movies` نظيف 100% وغير ملموس)
> **النسخة السحابية الحية على Google Drive:** مجلد `TBD-Movies-Remediation` (`1zM9qFoMmVLiMSdg_bJi1y78LRthjGrUz`)
> - حزمة مستودع `Git` الكامل بالتاريخ: `TBD-Movies-Audit.bundle` (`1z8lhmQlUlEkACTH_wyLPqcPnjLMbJvAP`)
> - أرشيف شجرة العمل الجاهزة للبناء والاختبار: `TBD-Movies-Audit-Worktree.zip` (`1VSpcGCfbpVi7riFs3Gb0j_Nw0XAx2150`)

---

## 1. القرارات المعمارية والتنظيمية المعتمدة نهائياً

1. **القرار الأول — مكان التنفيذ (`الخيار أ`):**
   التنفيذ حصرياً داخل النسخة المعزولة `/home/antigravity/TBD-Movies-Audit` مع `Commit` مستقل لكل دفعة ومزامنة فورية لملفَي `TBD-Movies-Audit.bundle` و `TBD-Movies-Audit-Worktree.zip` على Google Drive، وإبقاء `/home/antigravity/Mohaythem/TBD-Movies` نظيفاً تماماً بدون أي `git push` إلى GitHub إلا بأمر صريح بعد التحقق على Windows.
2. **القرار الثاني — مصير `TraktPage` في الموجة الخامسة (`الخيار أ`):**
   دمج إعدادات الاتصال وحالة المزامنة في `SettingsPage`، ودمج كافة القوائم في `MyListsPage`، وإلغاء تبويب `Trakt` المنفصل من `MainWindow.xaml`، وحذف الكود المكرر الميت (`~750` سطراً في `MyListsPage.Trakt.cs`) لإتمام هدف `[U9]` الأصلي.
3. **القرار الثالث — التراجع واستعادة النسخة الاحتياطية في الموجة السابعة (`الخيار أ`):**
   توصيل `IOperationHistoryUiActions.UndoOperation` و `DatabaseSafetyBackup.RestoreFromBackup` بأزرار واضحة ومحمية برسالة تأكيد داخل `OperationsLogPage` و `SettingsPage`.
4. **القرار الرابع — المزامنة التلقائية مع Google Drive (`الخيار أ`):**
   تحديث كلٍّ من `TBD-Movies-Audit.bundle` (`git bundle create --all`) و `TBD-Movies-Audit-Worktree.zip` في مكانهما (`gdrive mutate update`) داخل مجلد `TBD-Movies-Remediation` بعد إغلاق كل دفعة.
5. **القرار الخامس — التحقق المستمر عبر GitHub Actions CI على فرع `codex` فقط (`الخيار أ`):**
   دفع كل دفعة مختومة تلقائياً إلى فرع `refs/heads/codex` على GitHub (`git push origin HEAD:refs/heads/codex`) لتشغيل `.github/workflows/test.yml` على بيئة `windows-latest` الحقيقية (بناء الحل + بناء `TBDMovies.UI` `win-x64` + تشغيل كافة الاختبارات)، مع إبقاء فرع `main` ومستودع `/home/antigravity/Mohaythem/TBD-Movies` نظيفَين 100% دون أي مساس.

---

## 2. دورة حياة كل دفعة وقواعد الانضباط الصارمة

تمر كل دفعة من الدفعات الـ `18` بالمراحل الخمس التالية بالترتيب الحرفي دون تجاوز أي مرحلة:

1. **`معلّقة`**: لم يبدأ العمل البرمجي عليها بعد (ممنوع لمس أي ملف يخصها).
2. **`جارية`**: الدفعة الوحيدة المفتوحة حالياً قيد التنفيذ والتدقيق.
3. **`اتراجعت بـ Goldfish`**: اكتمل كود الدفعة واختباراتها، واجتازت مراجعة سمكة `Goldfish` معزولة (`Model: "inherit"`, سياق صفري، وإغلاق فوري للسمكة بحيث يبقى عدد الوكلاء النشطين = `0`)، وتم عمل `Commit` محلي وتحديث نسخة Google Drive، وهي الآن **متوقفة بانتظار ختم واعتماد المستخدم**.
4. **`اعتمدتها أنا`**: راجعها المستخدم واعتمدها صراحةً.
5. **`خلصت`**: أُغلقت الدفعة رسمياً وانتقل المؤشر إلى الدفعة التالية.

### القواعد الحديدية للتنفيذ:
- **دفعة واحدة فقط في كل مرة:** يُمنع منعاً باتاً فتح أو تعديل أي كود يخص الدفعة `N+1` قبل أن تصل الدفعة `N` إلى `اعتمدتها أنا` ثم `خلصت`.
- **انضباط الوكلاء الفرعيين:** لا يعمل أكثر من وكيل فرعي واحد (`Goldfish`) في اللحظة الواحدة، بوضع `Model: "inherit"`، ويُقتل (`kill`) فور تسليم تقريره.
- **تحديث هذا السجل:** يُحدَّث هذا الملف بعد كل انتقال في حالة أي دفعة ويُسجَّل فيه رقم الـ `Commit` ونتيجة `Goldfish`.

---

## 3. جدول تتبع الموجات الـ 7 والدفعات الـ 18

| الموجة | رقم الدفعة | عنوان الدفعة والبنود المعالجة | الملفات الأساسية المستهدفة | حالة الدفعة | Commit | مراجعة Goldfish |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **خط الأساس** | `0.0` | لقطة `2026-11-17` المرممة + إعادة هيكلة التوثيق وتقارير التدقيق المعتمدة | شجرة المشروع + `EGM.md` + تقارير التدقيق | `خلصت` | `5bf1da0` | `PASS` (`Goldfish 1..4`) |
| **الموجة 1** | **`1.1`** | إزالة سباق الخيوط على اتصال `SqliteConnection` المشترك في `FileOperationStore` وإظهار خطأ القراءة في `OperationsLogPage` (`SEC-19`, `C-06`, `#474`) | `FileOperationStore.cs`, `AppServices.cs`, `OperationsLogPage.xaml`, `OperationsLogPage.xaml.cs`, `WriteCoordinationTests.cs` | `خلصت` | `faed17e` | `PASS` (`Goldfish 1.1`) |
| **الموجة 1** | **`1.2`** | تأمين استخراج الترجمات وإصلاح بحث `SubDL` وتجمد نافذتها (`SEC-20`, `C-03`, `[U7]`, `#265`) | `SubtitleDownloadService.cs`, `SubDlClient.cs`, `SubtitleDialog.cs`, الاختبارات | `خلصت` | `c558260` | `PASS` (`Goldfish 1.2`) |
| **الموجة 1** | **`1.3`** | توحيد خزنة الأسرار `WindowsCredentialVault`، نقل مفتاح `SubDL`، والـ `VACUUM` المحكوم وتطهير النسخ الاحتياطية (`SEC-01..05`, `SEC-14..15`, `SEC-21..22`, `C-13`, `[L6]`) | `WindowsCredentialVault.cs`, `Windows*CredentialStore.cs`, `SqliteConnections.cs`, `DatabaseSafetyBackup.cs`, `SettingsService.cs`, `DiskPosterCache.cs`, `AppSettingsStore.cs`, `SettingsPage.xaml.cs`, `AppServices.cs` | `خلصت` | `bd23544` | `PASS` (`Goldfish 1.3`) |
| **الموجة 2** | **`2.1`** | إصلاح انحدار حفظ الثيم واللغة عبر إعادة التشغيل (`C-01`, `[P3]`) | `StartupPreferences.cs`, `AppServices.cs`, `SettingsService.cs`, `StartupPreferencesTests.cs` | `خلصت` | `8054690` | `PASS` (`Goldfish 2.1`) |
| **الموجة 2** | **`2.2`** | إصلاح عدّاد تحديث الحالة `LibraryRevisionTracker` (`C-04`) | `CatalogRepositories.cs`, `MaintenanceRepository.cs`, `AppServices.cs`, `HomePage.xaml.cs`, `LibraryPage.xaml.cs` | **`جارية`** | — | قيد التنفيذ |
| **الموجة 2** | `2.3` | إصلاح اشتراك `PlaybackEnded` المتماثل في صفحات التفاصيل (`C-05`) | `TVShowDetailsPage.xaml.cs`, `MovieDetailsPage.xaml.cs` | `معلّقة` | — | — |
| **الموجة 3** | `3.1` | إصلاح استرداد العمليات المتقطعة ومنع الموافقة الذاتية على المسارات (`SEC-16`, `C-09`, `#325..#327`) | `AppServices.cs`, `OperationHistoryService.cs`, الاختبارات | `معلّقة` | — | — |
| **الموجة 3** | `3.2` | إصلاح `NetworkReadGuard` ودعم الأقراص الشبكية المربوطة `DriveType.Network` (`C-10`) | `NetworkReadGuard.cs`, `NetworkHardeningTests.cs` | `معلّقة` | — | — |
| **الموجة 3** | `3.3` | إخراج `File.Exists` من قفل SQLite وخيط الواجهة + تجميع استعلام `HomePage` + إصلاح تصنيف `"ep"` (`C-11`, `C-12`) | `ImportService.cs`, `ReconciliationService.cs`, `PlaybackProgressRepository.cs`, `MediaDiscoveryService.cs`, `HomePage.xaml.cs`, `CheckLibraryPage.xaml.cs` | `معلّقة` | — | — |
| **الموجة 4** | `4.1` | تأمين تشغيل المشغلات الخارجية وتمرير `DurationSeconds` وحماية `MediaFileId <= 0` (`SEC-23`, `C-02` الأساسي, `#2, #167, #168`) | `PlayerProcessLauncher.cs`, `PlaybackTrackingService.cs`, الاختبارات | `معلّقة` | — | — |
| **الموجة 4** | `4.2` | إعادة ضبط المتتبعات الأربعة الأصلية `GSMTC` / `MPC-HC` / `VLC` / `mpv` (`[U10]`, `[U1]/[U2]`, `C-02`) | `WindowsMediaTimelineTracker.cs`, `MpcWebTimelineTracker.cs`, `VlcHttpTimelineTracker.cs`, `MpvPipeTimelineTracker.cs`, الاختبارات | `معلّقة` | — | — |
| **الموجة 5** | `5.1` | توحيد `ClientId` المخصص وإصلاح أخطاء استجابة `TraktDataClient` (`C-08`, `SEC-11`, `#290, #293`) | `TraktDataClient.cs`, `TraktDataClient.FavoritesAndCollection.cs`, `TraktDataClient.Watchlist.cs`, `TraktDataClient.Scrobble.cs` | `معلّقة` | — | — |
| **الموجة 5** | `5.2` | تحصين صمام الكاش ضد JSON التالف وإصلاح طوابير المزامنة ومفضلة المسلسلات (`SEC-06..10`, `C-07.1..5`) | `TraktSyncService.cs`, `PersonalLibraryRepository.cs`, `UnifiedTraktListsTests.cs` | `معلّقة` | — | — |
| **الموجة 5** | `5.3` | دمج إعدادات Trakt في `SettingsPage` والقوائم في `MyListsPage`، حذف تبويب `TraktPage` والكود الميت، وإصلاح زر الإزالة (`C-07.6`, `SEC-26`, القرار الثاني `أ`) | `MainWindow.xaml`, `MainWindow.xaml.cs`, `SettingsPage.*`, `MyListsPage.*`, `MediaCardContextMenu.cs`, `MovieDetailsPage.xaml.cs` | `معلّقة` | — | — |
| **الموجة 6** | `6.1` | السجل التشخيصي الآمن `AppDiagnosticsLog` وتطهير بقية بلوكات الفئة `(أ)` وتنقيح `ex.Message` (`SEC-17..18`, `SEC-24..25`, `Groups 1, 3, 4`) | `AppDiagnosticsLog.cs`, `App.xaml.cs`, صفحات الواجهة والخدمات المستهدفة | `معلّقة` | — | — |
| **الموجة 6** | `6.2` | إزالة المفاتيح المكررة في `LocalizationService`، استكمال التعريب، وتحصين `DatabaseMigrator` و `DiskPosterCache` (`C-14..16`, `SEC-12..13`) | `LocalizationService.cs`, `DatabaseMigrator.cs`, `DatabaseSafetyBackup.cs`, `DiskPosterCache.cs` | `معلّقة` | — | — |
| **الموجة 7** | `7.1` | إزالة الحزم والملفات الميتة، توصيل أزرار `UndoOperation` و `RestoreFromBackup`، واستخراج المساعدات المشتركة (`S-DEAD`, `S-ARCH`, القرار الثالث `أ`) | `TBDMovies.UI.csproj`, `OperationsLogPage.*`, `SettingsPage.*`, `WinUiClipboardService.cs`, موارد الأيقونات | `معلّقة` | — | — |
| **الموجة 7** | `7.2` | ضم `TBDMovies.QaSeed` إلى الحل وتحديثه لـ `Schema v23` وتحديث توقيع مثبّت Inno Setup (`C-17`, `[L8]`) | `TBDMovies.slnx`, `Seed.cs`, `TBD-Movies.iss` | `معلّقة` | — | — |

---

## 4. طريقة استخدام النسخة المرفوعة على Google Drive على جهازك (Windows)

### لاستنساخ أو تحديث مستودع `Git` الكامل محلياً من حزمة الـ Bundle:
```powershell
# أول مرة فقط (إنشاء مجلد مستودع Git كامل بالتاريخ والـ Commits):
git clone .\TBD-Movies-Audit.bundle TBD-Movies-Audit

# عند نزول أي دفعة جديدة (سحب أحدث الـ Commits من الملف المحدَّث):
cd .\TBD-Movies-Audit
git pull ..\TBD-Movies-Audit.bundle main
```

### لتشغيل البناء والاختبارات في بيئة معزولة آمنة:
```powershell
$env:TBDMOVIES_TEST_DATA_ROOT = "D:\TBDMovies_QA_Sandbox"
dotnet build .\TBDMovies_WinUI3\TBDMovies.slnx -c Release
dotnet test .\TBDMovies_WinUI3\TBDMovies.slnx -c Release --no-build
```
