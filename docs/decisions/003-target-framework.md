# ADR 003: Целевой фреймворк — .NET Framework 4.8

**Статус:** Принято

**Дата:** 2026-03-09

## Контекст

`Siemens.Engineering.dll` скомпилирована под .NET Framework. Необходимо выбрать, под какой рантайм собирать приложение.

Варианты:

1. **.NET Framework 4.8** — нативно совместим с Openness DLL
2. **.NET 6/8** — можно использовать через `<UseWindowsForms>` и `net8.0-windows`, но совместимость с COM/Interop требует дополнительной проверки

## Решение

Использовать **.NET Framework 4.8** (`<TargetFramework>net48</TargetFramework>`).

## Обоснование

- Siemens официально поддерживает и тестирует Openness API только под .NET Framework
- Исключает риск несовместимости на уровне P/Invoke и COM Interop
- .NET Framework 4.8 предустановлен на любой машине с Windows 10/11 и TIA Portal
- Современный синтаксис C# (record, pattern matching, nullable) доступен через `<LangVersion>latest</LangVersion>`

## Версионирование DLL

Siemens поставляет в каждой версии TIA Portal обновлённые DLL для всех поддерживаемых версий API:

```text
Portal V19/PublicAPI/
  V17/Siemens.Engineering.dll  ← V17 API surface, работает с V17/V18/V19 TIA Portal
  V18/Siemens.Engineering.dll
  V19/Siemens.Engineering.dll
```

**Правило:** брать DLL нужной версии API из установки **самой новой** TIA Portal, которую нужно поддерживать. Не из старой установки — там API устаревший.

Проект использует `libs/V17/Siemens.Engineering.dll` из TIA Portal V19, что обеспечивает совместимость с V17, V18 и V19.

## Последствия

- Недоступны некоторые библиотеки .NET 6+, но для данного инструмента это не критично
- Публикация как single-file exe невозможна (ограничение .NET Framework)
- Размещение в CI ограничено Windows-раннерами (и без того требуется TIA Portal)
- При добавлении поддержки V20+ — заменить DLL в `libs/V17/` на версию из V20-установки
