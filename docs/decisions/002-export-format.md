# ADR 002: Формат экспорта и структура файлов

**Статус:** Принято, часть про CSV заменена
**Дата:** 2026-03-09

> **Обновление 2026-11-10:** таблицы тегов хранятся в нормализованном SimaticML XML (`tags/*.xml`), а не в CSV. CSV теряет константы, флаги доступа и языки комментариев, а Openness-импорт таблиц принимает файл SimaticML. Разделы ниже про CSV оставлены как история решения.

## Контекст

Элементы TIA Portal экспортируются через Openness API несколькими способами:

1. **`ExportBlock` → XML** — работает для **всех** типов блоков и языков (LAD, FBD, AWL, SCL, STL), а также для UDT и tag tables
2. **`GenerateSource` → SCL** — читаемый текстовый источник; поддерживается для:
   - Блоков (FB, FC, OB) на SCL/ST
   - Data Blocks (DB) — структура описывается в SCL-синтаксисе вне зависимости от языка блока
   - UDT (PLC Data Types) — экспортируются как `TYPE ... END_TYPE`
   - **Не поддерживается** для Tag Tables
3. **`PlcTagTable.Export` → XML → CSV** — Tag Tables в TIA Portal поддерживают нативный импорт из CSV (`File → Import Tags`), поэтому инструмент конвертирует экспортированный Openness XML во внутренний CSV-формат TIA
4. **`Device.Export` → XML** — HW-конфигурация каждого устройства (CPU, IO-модули, PROFINET); только для отслеживания изменений, автоматический импорт не поддерживается

Конечная цель — **round-trip**: изменить файлы → импортировать обратно в TIA Portal.

Openness API поддерживает обратные операции:

- **`ImportBlock` ← XML** — для всех типов блоков
- **`GenerateBlocksFromSource` ← SCL** — компиляция SCL-источника обратно в блок
- **`PlcTagTable.Import` ← CSV** — нативный импорт тегов TIA Portal

## Решение

Четыре слоя экспорта по читаемости и возможности редактирования:

```text
<outputDir>/
  hw/                   # HW-конфигурация устройств — только для Git-diff, не импортируется
    CPU_1516.xml
    ET200SP_left.xml
  <PlcName>/            # По одной папке на каждый ПЛК в проекте
    src/                # SCL-источник: максимально читаемый и редактируемый
      Motor_FB.scl      # FB/FC/OB на SCL/ST
      Speed_FC.scl
      Main_OB.scl
      Motor_DB.scl      # Data Block (структура в SCL-синтаксисе)
      MotorType.scl     # UDT (TYPE ... END_TYPE)
    tags/               # Tag Tables в CSV
      Main_Tags.csv     # Name,Data Type,Logical Address,Comment
      Safety_Tags.csv
    xml/                # Блоки без SCL-представления (LAD/FBD/AWL)
      Conveyor_LAD.xml
```

Приоритет при экспорте элемента:

1. Блок/DB/UDT поддерживает `IGenerateSource` → `src/<Name>.scl`
2. Tag Table → `tags/<TableName>.csv` (через конвертацию из XML)
3. HW устройство → `hw/<DeviceName>.xml` (через `Device.Export`)
4. Иначе → `xml/<Name>.xml` (через `PlcBlock.Export`)

## CSV-формат тегов

TIA Portal при импорте ожидает конкретную структуру. Пример `Main_Tags.csv`:

```csv
Name,Data Type,Logical Address,Comment
Motor_Speed,Int,%MW100,Motor speed RPM
Motor_Running,Bool,%M0.0,Motor running status
Setpoint,Real,%MD200,
```

Формат нативный для TIA Portal — импорт без конвертации через GUI или `PlcTagTable.Import`.

## Расширение SCL-файлов

Инструмент использует расширение `.scl` — именно такое расширение генерирует сам TIA Portal через `GenerateSource`. Это обеспечивает совместимость при обратном импорте через `GenerateBlocksFromSource`.

## Технологические объекты (Technology Objects)

Технологические объекты TIA Portal (Motion Control Axis, PID Controller, Cam и др.) **уже покрываются** существующим экспортом:

- Конфигурация TO хранится в специальных Instance DB (тип `TechnologicalInstanceDB`)
- Эти DB попадают в `src/<Name>.scl` или `xml/<Name>.xml` как обычные блоки
- В будущем можно добавить папку `tech/` для их изоляции, если это потребуется

## Обоснование

- CSV читаем, редактируем в текстовом редакторе и Excel, хорошо diff-ится
- Нативная поддержка импорта CSV в TIA Portal обеспечивает round-trip без доп. инструментов
- SCL-файлы в `src/` — первичный редактируемый код; DB и UDT в SCL особенно ценны
- XML в `xml/` — только то, что не имеет лучшей альтернативы (LAD/FBD)
- `hw/` на уровне outputDir (не внутри PlcName) — потому что HW-конфиг существует на уровне устройства, не программного обеспечения; одно устройство может содержать несколько ПЛК или не содержать их вообще

## Round-trip workflow

```text
TIA Portal → GenerateSource / Export → src/*.scl / tags/*.csv / xml/*.xml / hw/*.xml
                                               ↓ редактирование в Git
TIA Portal ← GenerateBlocksFromSource / Import ← src/*.scl / tags/*.csv / xml/*.xml
             (hw/* — только вручную через TIA Portal GUI)
```

## Последствия

- Нужна конвертация XML тегов → CSV при экспорте (TagCsvConverter)
- Нужен этап нормализации XML (XmlNormalizer) для стабильного diff
- SCL-файлы при изменении требуют запущенного TIA Portal для компиляции обратно
- Для LAD/FBD diff в XML будет менее читаемым — принимается как ограничение прототипа
- HW-конфиг изменяется редко; команда договаривается о процессе ручного применения
