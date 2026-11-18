# TBD Movies — Documentation Index (`docs/README.md`)

هذا الفهرس يوثق الهيكل الرسمي المعتاد لوثائق المشروع ومصادر الحقيقة المعتمدة (`Single Source of Truth — SSOT`).

---

## 1. الوثائق المرجعية النشطة في الجذر (`Root Canonical SSOT`)

| الوثيقة | الدور والصلاحية الحصرية |
|---|---|
| [`/README.md`](../README.md) | واجهة التعريف العامة بالمشروع ومتطلبات التشغيل والتنزيل على GitHub. |
| [`/AGENTS.md`](../AGENTS.md) | **الدستور الأعلى للوكلاء**: قواعد السلوك، الحوكمة، بروتوكول التنفيذ، والثوابت البرمجية الملزمة في WinUI 3. |
| [`/HANDOFF.md`](../HANDOFF.md) | **وثيقة التسليم الوحيدة النشطة**: ملخص الحالة الراهنة (`Schema v23`, `1,143` اختباراً) للجلسة القادمة. |
| [`/BUGS.md`](../BUGS.md) | **المرجع الوحيد لحالة المشاكل**: سجل كافة البنود المفتوحة، المنفذة جزئياً، والمغلقة (`H / M / P / L / U`). |
| [`/PROGRESS.md`](../PROGRESS.md) | السجل الزمني المتسلسل للدفعات الهندسية المنفذة. |
| [`/EGM.md`](../EGM.md) | **مرجع العقود المعمارية الشامل (Elephant)**: عقود كافة ملفات المشروع الـ `333` وجداول SQLite العشرين (`v23`). |
| [`/AUDIT_FINDINGS_REPORT.md`](../AUDIT_FINDINGS_REPORT.md) | تقرير التدقيق الهندسي العميق الشامل ومصفوفة الحلول البديلة. |

---

## 2. شجرة مجلد `docs/` النشطة

- [`ROADMAP.md`](ROADMAP.md): خارطة الطريق الموحدة للأفكار والمقترحات المستقبلية الصافية (`V2` وما بعدها).
- [`adr/ADR-0016-trakt-read-only-cache.md`](adr/ADR-0016-trakt-read-only-cache.md): سجل القرار المعماري للمرحلة الأولى من Trakt (مع ترويسة `Partially Superseded by Schema v21–v23`).
- [`plans/2026-11/`](plans/2026-11/): خطط التصميم الهندسية السبع لمسارَي `[U8]` و `[U9]` *(مرجع تصميمي تاريخي؛ حالة الإغلاق تخضع حصراً لـ `/BUGS.md`)*.
- [`releases/`](releases/): ملاحظات وبيانات الإصدارات الرسمية من `0.10.0-rc.1` حتى [`RELEASE_NOTES_0.12.5.md`](releases/RELEASE_NOTES_0.12.5.md).
- [`source/The Idea-v3.md`](source/The%20Idea-v3.md): الوثيقة التأسيسية لفلسفة المنتج (`Local-First` وسلامة الملفات المحلية).
- [`archive/`](archive/README.md): الأرشيف التاريخي المعزول للقراءة المرجعية فقط (`winui3-v0.9-v0.11/` و `legacy-python/`).

---

## 3. العقود المعمارية والقانونية الحية المحفوظة في أرشيف `winui3-v0.9-v0.11` (ومستخلصة في `/EGM.md`)

تحتوي التقارير الخمسة التالية داخل [`archive/winui3-v0.9-v0.11/`](archive/winui3-v0.9-v0.11/) على خلفيات تصميمية وعقود لا تزال سارية في الكود الحالي:

1. **النظافة القانونية لرخصة `GPL-3.0` (`Clean-Room Policy`)**: [`SCANNER_RESEARCH_AND_COMPARISON.md`](archive/winui3-v0.9-v0.11/SCANNER_RESEARCH_AND_COMPARISON.md) — عدم نسخ أكواد من مشاريع خارجية مثل `Seanime` / `Habari`، والاكتفاء بدراسة البروتوكولات الرسمية وإعادة البناء الأصيل.
2. **عقد خزنة بيانات اعتماد TMDB**: [`TMDB_CREDENTIAL_STORAGE.md`](archive/winui3-v0.9-v0.11/TMDB_CREDENTIAL_STORAGE.md) — التخزين الحصري في `Windows Credential Manager`.
3. **عقد `DatabaseWriteGate` وترحيل القاعدة الآمن**: [`DATABASE_MIGRATION_HARDENING_REPORT.md`](archive/winui3-v0.9-v0.11/DATABASE_MIGRATION_HARDENING_REPORT.md).
4. **مرجع النسخ الاحتياطي المشار إليه في الكود (`DatabaseSafetyBackup.cs:137`)**: [`FUTURE_PLAN.md`](archive/winui3-v0.9-v0.11/FUTURE_PLAN.md).
5. **عقد `NetworkReadGuard` لمسارات الشبكة `UNC`**: [`UNC_NETWORK_HARDENING_REPORT.md`](archive/winui3-v0.9-v0.11/UNC_NETWORK_HARDENING_REPORT.md).

---

> [!CAUTION]
> **تحذير تشغيلي صارم (`TBDMOVIES_TEST_DATA_ROOT`)**:
> اسم متغير البيئة المعتمد في الكود (`AppServices.cs:77`) لعزل مسار البيانات والاختبارات هو **`TBDMOVIES_TEST_DATA_ROOT`** (بدون أي مسافة!). تم تصحيح كافة الإشارات القديمة الخاطئة التي كانت تحتوي على مسافة داخل ملفات الأرشيف والإصدارات.
