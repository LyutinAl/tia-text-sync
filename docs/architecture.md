# Архитектура tia-text-sync

## Верхнеуровневая схема

```text
┌──────────────────────────────────────────────────────────────────────┐
│                         WinForms GUI (MainForm)                       │
│  [TIA Portal ▼] [Обновить]  [Рабочая папка]  [Экспорт] [Импорт*]    │
│                         Строка состояния / путь к папке               │
└───────┬────────────────────────────────────────────┬─────────────────┘
        │ await _sta.RunAsync(...)                   │ Progress<string>
        ▼                                            ▼ (UI SyncContext)
┌───────────────┐      ┌──────────────────────────────────────────────┐
│  StaWorker    │      │               UI-поток                        │
│  (TIA-STA)    │      │  TreeView  ·  TextBox (лог)  ·  StatusStrip   │
│               │      └──────────────────────────────────────────────┘
│  TiaSession   │
│  ProjectSvc   │──▶ ExportService ──▶ src/ xml/ tags/
│  TreeBuilder  │──▶ HwExportService ▶ hw.xml
└───────────────┘
        │
        ▼
┌───────────────────┐
│  TIA Portal        │
│  (Openness COM)    │
└───────────────────┘

* Импорт — в разработке
```

## Потоковая модель

Openness API — COM out-of-process сервер. TIA Portal доставляет колбэки как Win32-сообщения в STA-поток вызывающей стороны. Если этот поток заблокирован внутри `WndProc` (как происходит с `Control.BeginInvoke` и `WindowsFormsSynchronizationContext.Post`), COM не может диспетчеризировать колбэки → **дедлок**.

**Решение — `StaWorker`**: выделенный STA-поток с «сырым» циклом Win32 (`GetMessage`). Работа отправляется через `PostThreadMessage` (hwnd=NULL) — такие сообщения обрабатываются **прямо в теле цикла**, не внутри `WndProc`. COM может свободно прокачивать очередь во время любого блокирующего вызова TIA.

```text
UI-поток               StaWorker (TIA-STA)
─────────              ───────────────────
await RunAsync(work)   GetMessage → work()   ← TIA вызовы здесь
UI ждёт Task           COM колбэки доставляются свободно
Task завершён ────────▶ продолжение на UI-потоке
```

## Структура пакетов

```text
TiaTextSync/
  Core/
    StaWorker.cs         — выделенный STA-поток, PostThreadMessage-цикл
    TiaSession.cs        — подключение к TIA Portal / запуск нового процесса
    WorkspaceService.cs  — рабочая папка (Git-репозиторий), персистентность
    Logger.cs            — потокобезопасный файловый лог с временными метками

  Services/
    ProjectService.cs    — открытие проекта, перечисление устройств и ПЛК
    ExportService.cs     — экспорт блоков (SCL/AWL/XML), тегов (CSV), UDT
    HwExportService.cs   — снимок HW-конфигурации в XML (diff only)
    XmlNormalizer.cs     — удаление нестабильных полей из XML
    TagCsvConverter.cs   — конвертация TIA ML XML → TIA-native CSV

  UI/
    MainForm.cs          — главное окно WinForms
    TreeBuilder.cs       — построение TreeNode из COM-объектов TIA

  Program.cs             — точка входа, инициализация Logger, Application.Run
```

## Компоненты

### StaWorker

Выделенный STA-поток с «сырым» Win32-циклом сообщений (`GetMessage/PostThreadMessage`). Все обращения к TIA Portal Openness API выполняются через `RunAsync(work)`. Позволяет избежать дедлока COM ↔ WndProc.

### TiaSession

Управляет жизненным циклом TIA Portal:

- **Подключение к работающему TIA Portal** — через `TiaPortal.GetProcesses()`, не трогает проекты
- **Запуск нового экземпляра** — если TIA Portal не запущен; закрывает его при `Dispose()`

Создаётся и используется исключительно на потоке `StaWorker`.

### WorkspaceService

Хранит путь к корневой папке Git-репозитория. Путь сохраняется в `%AppData%\TiaTextSync\workspace.txt` между запусками.

### ProjectService

Открывает TIA-проект, перечисляет устройства и ПЛК:

- `GetOpenProject()` — возвращает первый уже открытый проект
- `OpenProject()` — открывает файл или переиспользует уже открытый (по пути)
- `GetAllPlc(project)` — `Device → DeviceItem → SoftwareContainer → PlcSoftware`
- `GetDeviceGroups(project)` — группировка ПЛК по устройствам для экспорта

### ExportService

Экспортирует содержимое одного `PlcSoftware` в три подпапки:

| Метод | Источник | Папка | Расширение | Round-trip |
| ----- | -------- | ----- | ---------- | ---------- |
| `ExportBlockAsSrc` | `IGenerateSource.GenerateSource()` | `src/` | `.scl` / `.awl` | ✅ |
| `ExportBlockAsXml` | `PlcBlock.Export()` | `xml/` | `.xml` | ✅ `ImportBlock` |
| `ExportTagTable` | `PlcTagTable.Export()` → CSV | `tags/` | `.csv` | ✅ |
| `ExportType` | `PlcType` (UDT) | `src/` или `xml/` | `.scl` / `.xml` | ✅ |

Примечание: STL-блоки экспортируются с расширением `.awl` (TIA Portal отклоняет `.scl` для STL).

### HwExportService

Строит XML-снимок аппаратной конфигурации устройства из свойств Openness API (`Name`, `TypeIdentifier`, `PositionNumber`, `IsPlugged`). Используется только для Git diff — автоматический импорт не поддерживается.

Вывод: `<deviceDir>/hw.xml`

### TreeBuilder

Строит дерево `TreeNode` из COM-объектов TIA, выполняется целиком на потоке `StaWorker`. `TreeNode` — объект данных (не UI-контрол), создавать вне UI-потока безопасно.

### XmlNormalizer

Удаляет нестабильные поля из XML-экспорта: `ModifiedDate`, `CreationDate`, `CompileDate`, `CodeModifiedDate`, `InterfaceModifiedDate`, `ParameterModified`, `StructureModified`. Делает `git diff` стабильным при повторном экспорте.

### TagCsvConverter

Конвертирует Simatic ML XML-экспорт таблицы тегов в TIA-native CSV (`Name,Data Type,Logical Address,Comment`). Импортируется через `File → Import Tags`.

## Структура файлов в репозитории

```text
<workspace>/                    ← корень Git-репозитория
  <SafeDeviceName>/             ← имя устройства (символы "/" заменены)
    hw.xml                      ← снимок HW-конфигурации (diff only)
    <PlcName>/
      src/
        <BlockName>.scl         ← SCL/DB/UDT с ST-исходником
        <BlockName>.awl         ← STL-блоки
      tags/
        <TableName>.csv         ← таблицы тегов
      xml/
        <BlockName>.xml         ← LAD/FBD блоки, UDT без ST
```

## Технологический стек

| Компонент | Выбор | Причина |
| --------- | ----- | ------- |
| Runtime | .NET Framework 4.8 | Требование Openness API |
| Язык | C# (LangVersion latest) | Современный синтаксис: record, pattern matching, using var |
| UI | WinForms | Встроен в .NET 4.8, минимальные зависимости |
| API | Siemens TIA Openness V17 | Единственный официальный способ программного доступа |
| DLL | V17 из установки TIA Portal V19 | Актуальная версия DLL со всеми исправлениями |
| SCL | `.scl` / `.awl` | Расширения, генерируемые самим TIA Portal через `GenerateSource` |

## Ключевые ограничения платформы

- Openness API требует x64 и работает только на Windows
- Пользователь должен состоять в группе `Siemens TIA Openness`
- Нельзя открыть проект, уже открытый в GUI — нужно подключаться к существующему процессу
- Экспорт блоков — синхронная операция; для больших проектов занимает несколько минут
