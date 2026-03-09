# Архитектура tia-text-sync

## Верхнеуровневая схема

```text
┌─────────────────────────────────────────────────────────────┐
│                        CLI (Program.cs)                      │
│           --project <path> --output <dir>                    │
└────────────┬──────────────────────────────────┬─────────────┘
             │                                  │
             ▼                                  ▼
┌────────────────────────┐         ┌────────────────────────┐
│      TiaSession        │         │     ProjectReader       │
│  Подключение к TIA     │────────▶│  Открытие проекта,     │
│  Portal (или запуск)   │         │  обход дерева блоков   │
└────────────────────────┘         └────────────┬───────────┘
                                                │
                              ┌─────────────────┼──────────────────┐
                              │                 │                  │
                              ▼                 ▼                  ▼
                   ┌──────────────────┐  ┌──────────────┐  ┌──────────────────┐
                   │  ExportService   │  │ HwExport     │  │  (будущее)       │
                   │  (per PlcSoft)   │  │ Service      │  │  ImportService   │
                   │  src/ xml/ tags/ │  │  hw/         │  │                  │
                   └────────┬─────────┘  └──────┬───────┘  └──────────────────┘
                            │                   │
                 ┌──────────┼──────────┐         │
                 ▼          ▼          ▼         ▼
         ┌──────────┐ ┌──────────┐ ┌──────────────────┐
         │   Xml    │ │  Tag     │ │  XmlNormalizer   │
         │Normalizer│ │Csv       │ │  (для hw/ тоже)  │
         │          │ │Converter │ └──────────────────┘
         └──────────┘ └──────────┘
```

## Компоненты

### TiaSession

Управляет жизненным циклом подключения к TIA Portal. Поддерживает два режима:

- **Подключение к работающему TIA Portal** — через `TiaPortal.GetProcesses()` (не трогает открытые проекты)
- **Запуск нового экземпляра** — если TIA Portal не запущен; диспозит при завершении работы

### ProjectReader

Обходит дерево объектов TIA-проекта:

- `FindOpenProject()` — проверяет, не открыт ли уже проект в TIA Portal
- `OpenProject()` — открывает проект или возвращает уже открытый
- `GetAllPlc(project)` — перебирает `Device → DeviceItem → SoftwareContainer → PlcSoftware`
- `ReadBlocks()` — рекурсивно собирает метаданные блоков (`BlockInfo`) для вывода списка

### ExportService

Экспортирует содержимое одного `PlcSoftware` в три подпапки:

| Метод | Источник | Папка | Расширение | Round-trip |
| ----- | -------- | ----- | ---------- | ---------- |
| `ExportBlockAsSt` | `IGenerateSource.GenerateSource()` | `src/` | `.scl` | ✅ `GenerateBlocksFromSource` |
| `ExportBlockAsXml` | `PlcBlock.Export()` | `xml/` | `.xml` | ✅ `ImportBlock` |
| `ExportTagTable` | `PlcTagTable.Export()` → CSV | `tags/` | `.csv` | ✅ `PlcTagTable.Import` |
| `ExportType` | `PlcType` (UDT) | `src/` или `xml/` | `.scl` / `.xml` | ✅ |

### HwExportService

Экспортирует HW-конфигурацию на уровне проекта:

- Итерирует `Project.Devices`
- Каждое устройство → `hw/<DeviceName>.xml` через `Device.Export()`
- XML нормализуется через `XmlNormalizer`
- Только для отслеживания в Git — автоматический импорт не поддерживается

### XmlNormalizer

Постобработка экспортированных XML-файлов:

- Удаляет нестабильные поля: `ModifiedDate`, `CreationDate`, `CompileDate`, `CodeModifiedDate`, `InterfaceModifiedDate`, `ParameterModified`, `StructureModified`
- Делает diff стабильным при повторном экспорте без изменений

### TagCsvConverter

Конвертирует XML-экспорт TIA ML тег-таблицы в CSV-формат TIA Portal:

- Читает `SW.Tags.PlcTag` элементы из Simatic ML XML
- Формирует CSV: `Name,Data Type,Logical Address,Comment`
- Нативный формат — импортируется без конвертации через `File → Import Tags`

## Структура файлов

```text
<outputDir>/
  hw/
    <DeviceName>.xml          # Device.Export() → XmlNormalizer
  <PlcName>/
    src/
      <BlockName>.scl         # IGenerateSource.GenerateSource()
    tags/
      <TableName>.csv         # PlcTagTable.Export() → TagCsvConverter
    xml/
      <BlockName>.xml         # PlcBlock.Export() → XmlNormalizer
```

## Технологический стек

| Компонент | Выбор | Причина |
| --------- | ----- | ------- |
| Runtime | .NET Framework 4.8 | Требование Openness API |
| Язык | C# (LangVersion latest) | Современный синтаксис: record, pattern matching, using var |
| API | Siemens TIA Openness V17 | Единственный официальный способ программного доступа к проекту |
| DLL | V17 из установки TIA Portal V19 | Последняя версия DLL содержит все исправления API для V17 |
| Формат SCL | `.scl` | Расширение, генерируемое самим TIA Portal через `GenerateSource` |

## Ключевые ограничения платформы

- Openness API требует x64 и работает только на Windows
- Нельзя открыть проект, уже открытый в GUI — нужно подключаться к существующему процессу
- Экспорт блоков — синхронная операция; для больших проектов может быть медленным
- `record`-типы на .NET 4.8 требуют полифилла `IsExternalInit` ([Polyfills.cs](../Polyfills.cs))
