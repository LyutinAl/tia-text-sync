# tia-text-sync

Прототип инструмента командной разработки PLC-проектов (Siemens TIA Portal) с поддержкой Git.

## Проблема

TIA Portal сохраняет проекты в бинарном или XML-формате, плохо приспособленном для diff/merge. Команда не может нормально работать с историей изменений и ревью кода.

## Решение

`tia-text-sync` подключается к TIA Portal через Openness API и:

1. Извлекает блоки (FB, FC, DB, OB) в нормализованное текстовое представление
2. Раскладывает их по файлам в структуру, удобную для Git
3. Позволяет сравнивать изменения стандартными Git-инструментами

## Быстрый старт

### Требования

- Windows 10/11 x64
- Siemens TIA Portal V17
- .NET Framework 4.8
- Пользователь должен состоять в группе **Siemens TIA Openness**

### Сборка

```bash
dotnet build tia-text-sync.sln
```

### Запуск

```bash
tia-text-sync.exe --project "C:\Projects\MyPLC\MyPLC.ap17" --output "./export"
```

> На текущем этапе прототип запускает TIA Portal и выводит список блоков проекта в консоль.

## Документация

- [Обзор проекта](docs/overview.md)
- [Архитектура](docs/architecture.md)
- [Решения (ADR)](docs/decisions/)

## Статус

**Прототип / WIP** — реализован базовый запуск TIA Portal через Openness API.
