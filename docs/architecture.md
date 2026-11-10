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
    OpennessResolver.cs  — загрузка сборок Openness из установки TIA Portal V21

  Services/
    ProjectService.cs    — открытие проекта, перечисление устройств и ПЛК
    ExportService.cs     — экспорт блоков (SCL/AWL/DB/XML), тегов (XML), UDT
    HwExportService.cs   — снимок HW-конфигурации в XML (diff only)
    XmlNormalizer.cs     — удаление нестабильных полей из XML

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
| `ExportBlockAsSrc` | `IGenerateSource.GenerateSource()` | `src/` | `.scl` / `.awl` / `.db` | ✅ |
| `ExportBlockAsXml` | `PlcBlock.Export()` | `xml/` | `.xml` | ✅ `ImportBlock` |
| `ExportTagTable` | `PlcTagTable.Export()` | `tags/` | `.xml` | ✅ `PlcTagTableComposition.Import` |
| `ExportType` | `IGenerateSource.GenerateSource()` | `src/` | `.scl` | ✅ |

Примечания:

- Способ экспорта блока определяется по `ProgrammingLanguage`: `SCL`, `STL` и `DB` идут в `src/`, все остальные языки (LAD, FBD, GRAPH, F-блоки) в `xml/`. Проверка `is IGenerateSource` не подходит, потому что этот интерфейс реализован самим `PlcBlock`.
- STL-блоки экспортируются с расширением `.awl`, а DB с расширением `.db`: для них TIA Portal отклоняет `.scl`.
- Несогласованные (не скомпилированные) блоки TIA Portal не экспортирует, проект нужно предварительно скомпилировать.
- Файлы создаются во временной папке и заменяют файлы в рабочей папке только после успешного экспорта.
- После экспорта из `src/`, `xml/`, `tags/` удаляются файлы `.scl`, `.awl`, `.db`, `.xml`, `.csv`, которых нет в ПЛК. Файлы элементов, экспорт которых завершился ошибкой, сохраняются.

### HwExportService

Строит XML-снимок аппаратной конфигурации устройства из свойств Openness API (`Name`, `TypeIdentifier`, `PositionNumber`, `IsPlugged`). Используется только для Git diff — автоматический импорт не поддерживается.

Вывод: `<deviceDir>/hw.xml`

### TreeBuilder

Строит дерево `TreeNode` из COM-объектов TIA, выполняется целиком на потоке `StaWorker`. `TreeNode` — объект данных (не UI-контрол), создавать вне UI-потока безопасно.

### XmlNormalizer

Удаляет нестабильные поля из XML-экспорта: `Created`, `ModifiedDate`, `CreationDate`, `CompileDate`, `CodeModifiedDate`, `InterfaceModifiedDate`, `ParameterModified`, `StructureModified`. Делает `git diff` стабильным при повторном экспорте.

## Структура файлов в репозитории

```text
<workspace>/                    ← корень Git-репозитория
  <SafeDeviceName>/             ← имя устройства (символы "/" заменены)
    hw.xml                      ← снимок HW-конфигурации (diff only)
    <PlcName>/
      src/
        <BlockName>.scl         ← SCL-блоки и UDT
        <BlockName>.db          ← DB
        <BlockName>.awl         ← STL-блоки
      tags/
        <TableName>.xml         ← таблицы тегов (SimaticML)
      xml/
        <BlockName>.xml         ← LAD/FBD/GRAPH и F-блоки
```

## Технологический стек

| Компонент | Выбор | Причина |
| --------- | ----- | ------- |
| Runtime | .NET Framework 4.8 | Требование Openness API |
| Язык | C# (LangVersion latest) | Современный синтаксис: record, pattern matching, using var |
| UI | WinForms | Встроен в .NET 4.8, минимальные зависимости |
| API | Siemens TIA Openness V21 | Единственный официальный способ программного доступа |
| DLL | `Siemens.Engineering.Base` и `Step7` из `PublicAPI\V21\net48` | В V21 API разбит на отдельные сборки. Для компиляции берутся из `libs/V21`, при запуске загружаются из установки TIA Portal |
| SCL | `.scl` / `.awl` | Расширения, генерируемые самим TIA Portal через `GenerateSource` |

## Ключевые ограничения платформы

- Openness API требует x64 и работает только на Windows
- Пользователь должен состоять в группе `Siemens TIA Openness`
- Нельзя открыть проект, уже открытый в GUI — нужно подключаться к существующему процессу
- Экспорт блоков — синхронная операция; для больших проектов занимает несколько минут
