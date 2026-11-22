# Архитектура

Документ для того, кто будет менять Pepa: как устроены модули, как исполняется сценарий, где лежат данные и в каком
файле что править. Почему сделано именно так — в [DECISIONS.md](DECISIONS.md) (ссылки вида D-0NN). Пошаговые рецепты
(«как добавить блок, адрес API, настройку…») — в `AGENTS.md` в корне репозитория; стратегия и карта тестов — в
[TESTING.md](TESTING.md); ручные проверки на Windows — в [CHECKLIST.md](CHECKLIST.md).

Имена типов, файлов и методов даны как в коде. Пути — от корня репозитория.

## 1. Общая картина

### 1.1. Слои

```
 ┌─────────────── Pepa.exe (Windows, WPF) ───────────────┐     ┌──────── pepa-server (Pepa.Server) ────────┐
 │ окно: WebView2 + мост (выбор папки, «открыть папку»), │     │ консоль, без окна; интерфейс — в обычном  │
 │ значок в трее, автозапуск, завершение сеанса, Win+L   │     │ браузере (разработка, CI, e2e)            │
 └───────────────────────────┬───────────────────────────┘     └─────────────────────┬─────────────────────┘
                             │ в том же процессе                                     │
                             ▼                                                       ▼
 ┌──────────────────────────────────── Pepa.Host (PepaHost) ───────────────────────────────────────────────┐
 │ Kestrel на 127.0.0.1: /api (JSON, токен X-Pepa-Token) · /api/stream (SSE) · интерфейс из wwwroot        │
 │ настройки и ключи · резервные копии и срок хранения · уведомления · питание · расширения · примеры      │
 └───────────────┬─────────────────────────────────────────────────────────────────────────┬───────────────┘
                 ▼                                                                         ▼
 ┌──────────── Pepa.Engine (ProcessEngine) ─────────────┐   ┌───────────────────── Адаптеры (блоки) ──────────────────────┐
 │ процессы, маркеры, шаги, ожидания, события,          │   │ FileSystem (файлы, слежение за папками) · Tables (xlsx/csv) │
 │ ресурсы, автозапуск, восстановление, решения,        │   │ Http · Email (IMAP/SMTP) · Browser (Playwright + AdsPower)  │
 │ встроенные логические блоки и блоки платформы        │   │ Telegram (MTProto) · Desktop (+ Desktop.Windows: FlaUI)     │
 └───────────────┬──────────────────────────────────────┘   │ Assistant (Claude API) · расширения из plugins/             │
                 ▼                                          └─────────────────────────────────────────────────────────────┘
 ┌──── Pepa.Storage.Sqlite ────┐   ┌────────────────── Pepa.Core (без хранилища и интерфейса) ───────────────────┐
 │ pepa.db: WAL, миграции,     │   │ модель сценария · описание и контракт блока · NodeRegistry · проверка       │
 │ одна транзакция записи      │   │ сценария · выражения (Jint) · контракт расширений · DPAPI · события         │
 └─────────────────────────────┘   └─────────────────────────────────────────────────────────────────────────────┘

 ui/ (React 19 + TypeScript) ── только HTTP API и SSE; собирается в src/Pepa.Host/wwwroot
```

Движок, хранилище, адаптеры и сервер API работают в одном процессе с окном (или с консольным сервером). Интерфейс
получает данные только через локальный API (D-003, D-004); тот же API доступен своей программе на этом компьютере.

### 1.2. Проекты

| Проект | Назначение | Зависит от |
|---|---|---|
| `Pepa.Core` | модель сценария (`Model/`), описание блока и контракт исполнителя (`Nodes/`: `NodeTypeDescriptor`, `INodeExecutor`, `NodeResult`, `NodeRegistry`), проверка (`Validation/`), выражения Jint (`Expressions/`), контракты событий (`Events/`), контракт расширений (`Plugins/IPepaPlugin.cs`), DPAPI (`Security/Secrets.cs`), сравнение текста (`Text/TextMatching.cs`), `PepaJson` | Jint, System.Security.Cryptography.ProtectedData |
| `Pepa.Engine` | `ProcessEngine` (partial-файлы `ProcessEngine.*.cs`), `ProcessSlot`, `InputResolver`, контракт хранилища (`Persistence/`), `ScenarioService`, расписания (`Scheduling/`), маскировка секретов (`SecretMasker`), блоки логики и платформы (`Nodes/`) | Core |
| `Pepa.Storage.Sqlite` | `SqliteStore` / `SqliteWrite`: схема, миграции, запросы, резервное копирование | Engine, Microsoft.Data.Sqlite |
| `Pepa.Adapters.FileSystem` | рабочая папка (`IWorkspace`), файловые блоки, `file.wait`, слежение за папками (`FolderWatcher`) | Core |
| `Pepa.Adapters.Tables` | таблицы .xlsx/.xlsm и CSV/TSV в рабочей папке | Core, FileSystem, DocumentFormat.OpenXml |
| `Pepa.Adapters.Http` | `http.get` (только чтение) и `http.send` | Core |
| `Pepa.Adapters.Email` | `EmailService`: IMAP (новые письма — события `email`), SMTP-отправка с проверкой по «Отправленным» | Core, FileSystem, MailKit |
| `Pepa.Adapters.Browser` | `BrowserSessions`, `AdsPowerClient`, блоки браузера, `browser.waitAny`, запись действий (`BrowserRecorder`) | Core, FileSystem, Microsoft.Playwright |
| `Pepa.Adapters.Telegram` | `TelegramService` (WTelegramClient, свой аккаунт), блоки Telegram, прокси | Core, FileSystem, WTelegramClient |
| `Pepa.Adapters.Desktop` | блоки окон, мыши и клавиатуры за интерфейсом `IDesktop`; `DesktopRecorder` | Core |
| `Pepa.Adapters.Desktop.Windows` | `UiaDesktop` (FlaUI UIA3 + SendInput), `UiaInputMonitor` (перехват ввода для записи); `net10.0-windows` | Adapters.Desktop, FlaUI.UIA3 |
| `Pepa.Assistant` | ИИ-помощник конструктора: `ScenarioAssistant`, `ScenarioPrompt`, `DraftConverter` | Core, Anthropic SDK |
| `Pepa.Host` | композиция (`PepaHost`), API, SSE, безопасность, настройки и ключи, резервные копии, срок хранения, снимки, уведомления, питание, расширения, примеры, автозапуск (`Startup/AutostartEntry`), блокировка экземпляра, журнал | Engine, Storage.Sqlite, все адаптеры кроме Desktop.Windows, Assistant; ASP.NET Core |
| `Pepa.Server` | `pepa-server`: консольный запуск `PepaHost` | Host |
| `Pepa.Desktop` | `Pepa.exe`: WPF + WebView2, трей (WinForms `NotifyIcon`), автозапуск, завершение сеанса, блокировка компьютера; `net10.0-windows` | Host, Adapters.Desktop.Windows, Microsoft.Web.WebView2 |
| `ui/` | интерфейс: React 19, TypeScript, Vite, TanStack Query, React Flow (`@xyflow/react`), lucide-react | только HTTP API |
| `samples/Pepa.Plugins.Sample` | образец расширения (`sample.textcase`, `sample.appendline`) и правила для авторов | Core (`Private=false`) |
| `tests/` | xUnit v3 по проекту на модуль (`Pepa.*.Tests`); `ui/src/**/*.test.ts` (vitest) и `ui/e2e/smoke.cjs` (Playwright) | — |

Сборка: `TreatWarningsAsErrors`, версии пакетов только в `Directory.Packages.props`, общие свойства —
`Directory.Build.props`/`.targets` (Playwright-драйвер — только у исполняемых проектов). Core, Engine, Storage,
адаптеры и их проверки кроссплатформенны; Windows-проекты собираются на Linux (`EnableWindowsTargeting`), но не
запускаются.

### 1.3. Главные правила (на них держится всё остальное)

- Внешнее действие записывается на диск как «выполняется» **до** вызова; итог шага — одной транзакцией (D-006, D-010).
- Неизвестный итог не повторяется вслепую: сначала проверка (`VerifyAsync`), иначе решает человек (D-010, D-032).
- После перезапуска ничего не продолжается само: всё незавершённое — на паузе или ждёт решения (D-010).
- Неоднозначность (событие подошло нескольким, условие не вычислилось) — не угадываем, спрашиваем (D-017, D-079).
- Секреты не показываются и не пишутся в журналы; в хранилище — настоящие значения для проверки (D-055…D-059).
- Файловые действия — только внутри рабочей папки (D-014). Один экземпляр на папку данных (D-013).

### 1.4. Где что менять

| Задача | Где |
|---|---|
| новый блок с внешним действием | адаптер `src/Pepa.Adapters.<X>/` + `Register<X>Nodes` + цепочка в `PepaHost.StartCoreAsync` |
| новый логический блок | `src/Pepa.Engine/Nodes/` + `RegisterLogicNodes()`/`RegisterFlowNodes()` |
| поведение шага, итоги, повторы | `src/Pepa.Engine/ProcessEngine.Execution.cs` (`ExecuteActionAsync`, `CommitResultAsync`) |
| события, сопоставление, конфликты | `ProcessEngine.Events.cs`; ожидания, ресурсы, дочерние процессы — `ProcessEngine.Platform.cs` |
| автозапуск | `ProcessEngine.Triggers.cs`, `Scheduling/ScheduleCalculator.cs` |
| восстановление и решения пользователя | `ProcessEngine.Recovery.cs`; список доступных решений — `GetAllowedActions` в `ProcessEngine.cs` |
| схема базы | `SqliteStore.Schema.cs` (миграции), `SqliteWrite.cs`, `SqliteMapping.cs`, `Persistence/Records.cs` |
| адрес API | `src/Pepa.Host/Api/ApiEndpoints.cs`, `Dtos.cs`, маскировка — `ProcessMapper.cs` |
| настройка платформы | `Settings/SettingsService.cs` (`PlatformSettings`), `PUT /api/settings`, `ui/src/pages/SettingsPage.tsx` |
| экран интерфейса | `ui/src/pages/`, маршрут — `ui/src/router.ts`, `ui/src/App.tsx` |
| редактор полей блока | `ui/src/pages/editor/FieldEditor.tsx`, `Inspector.tsx` |

## 2. Модель сценария и контракт блока

### 2.1. Сценарий

`ScenarioDefinition` (`src/Pepa.Core/Model/ScenarioDefinition.cs`) — граф, хранится в JSON (`PepaJson`: camelCase,
перечисления строками, кириллица без экранирования):

- `SchemaVersion` (= 1; миграций формата нет), `Id`, `Name`, `Description`, `RunTitle` (шаблон названия запуска, D-081);
- `Variables` — `VariableDefinition`: `Name`, `Type` (`any`, `text`, `number`, `boolean`, `list`, `object`), `Default`,
  `IsInput` (спрашивается при запуске), `Secret` (D-055), `Lookup` (выбор при запуске, например профили AdsPower);
- `Nodes` — `NodeDefinition`: `Id`, `Type`, `Label`, `Position`, `Config` (параметры блока, JSON), `Settings`
  (`NodeSettings`: `RetryCount`, `RetryDelaySeconds` = 5, `TimeoutSeconds`, `Breakpoint`), `Note`;
- `Edges` — `EdgeDefinition`: из именованного выхода (`SourcePort`) блока `Source` во вход блока `Target`; у каждого
  выхода не больше одной связи;
- `Editor` — данные только конструктора (группы блоков, D-040), движок их не читает.

Стандартные выходы — `Ports`: `out`, `error`, `true`, `false`, `event`, `timeout`, `done`, `item`. `ScenarioGraph` —
индексированный граф (`Next(node, port)`, `LoopBody`).

**Публичный контракт, который нельзя переименовывать:** типы блоков, имена полей, значения вариантов `Select`, id
выходов, имена членов перечислений (хранятся строками), форматы ключей (`origin_key`, `external_id`). От них зависят
сохранённые сценарии, экспорт, примеры, запись действий и подсказки помощника. Новое поле — только со значением по
умолчанию, сохраняющим прежнее поведение.

`ScenarioService` (`src/Pepa.Engine/ScenarioService.cs`) — создание, сохранение с оптимистичной версией
(`expectedVersion` → `ConcurrencyException`), копия, удаление (вместе с автозапусками). Сохранение **не** проверяет
сценарий: черновик с ошибками можно сохранить, запустить — нельзя.

### 2.2. Описание блока (`NodeTypeDescriptor`)

`src/Pepa.Core/Nodes/NodeTypeDescriptor.cs` — всё о блоке, кроме поведения. По нему строятся палитра, форма свойств,
проверка, вычисление параметров (`InputResolver`), список доступных решений и каталог ИИ-помощника.

| Свойство | Смысл |
|---|---|
| `Type`, `Category`, `Title`, `Description`, `Icon` | тип (`префикс.имя`), раздел палитры, тексты, имя иконки lucide |
| `Effect` | `None` — без ввода-вывода; `ReadOnly` — только читает мир; `External` — меняет мир. От него зависит весь путь шага |
| `SupportsVerification` | `VerifyAsync` умеет сказать «выполнено / не выполнено» без повтора |
| `IsStart`, `IsEnd`, `Flow` | начало/конец; `Normal`, `Split` («Параллельно»), `Join` («Дождаться всех ветвей») |
| `Outputs` | `PortDescriptor`: `Id`, `Label`, `Kind` (`Normal`, `Error`, `Optional` — может остаться неподключённым), `LabelField` (подпись выхода из поля; выход скрыт, пока поле пусто), `NothingDone` (уход по этому выходу значит «действие ничего не изменило») |
| `Fields` | `FieldDescriptor`: `Name`, `Label`, `Kind`, `Required`, `Default`, `Options`, `Min`/`Max`, `VisibleWhen`, `Lookup` (выбор значения в конструкторе), `SharedState` (общее состояние — параллельные ветви не могут делить, например вкладку браузера) |
| `SummaryField`, `ResourceHint` | что показать на карточке; подсказка о занимаемом ресурсе |

`SupportsRetry` = `Effect != None`: настройки повтора действуют только для блоков с эффектом.

Виды полей (`FieldKind`) и как их вычисляет `InputResolver` (в шаг попадают только **видимые** поля описания;
необъявленные ключи `Config` читаются из `context.Node.Config`):

| Вид | При выполнении |
|---|---|
| `Template`, `MultilineTemplate`, `Path` | шаблон `{{ выражение }}` подставляется |
| `Url` | `RenderUrl`: подставленные части кодируются; пустая подстановка, смена хоста и `..` — ошибка |
| `Expression` | вычисляется JavaScript |
| `Condition` | вычисляется, должно дать `true`/`false` |
| `Text`, `Code`, `MatchCondition`, `Number`, `Boolean`, `Select`, `Variable`, `Time`, `Scenario`, `Seconds` | копируется как есть (`Code` и `MatchCondition` выполняются самим блоком/маршрутизатором) |

### 2.3. Исполнитель (`INodeExecutor`)

`src/Pepa.Core/Nodes/INodeExecutor.cs`. Исполнитель **без состояния**: состояние процесса — в памяти блока
(`Success.Memory`) или в снимке (`Checkpoint`).

| Член | Когда вызывается | Правило |
|---|---|---|
| `Descriptor` | регистрация | неизменяемый, детерминированный |
| `Validate(node)` | проверка сценария | доп. ошибки конфигурации → код `node.config`; значения с `{{` не проверять |
| `PrepareAsync` | до записи «выполняется» | только чтение: снимок «до», ключи от повторов; итог → `step.Checkpoint` |
| `ExecuteAsync` | само действие | `NodeInputException` — только до любого эффекта; перед эффектом — `ThrowIfCancellationRequested` |
| `VerifyAsync` | итог неизвестен; решение «Проверить» | только чтение, без повтора; `Done` — только если уверен, `NotDone` — только если точно не выполнено, иначе `Inconclusive` |
| `RequiredResource(input)` | перед действием | монопольный ресурс (`ResourceRequirement(Key, Title)`); движок занимает его сам |
| `ExpectedDuration(input)` | расчёт тайм-аута | для действий дольше общего тайм-аута |

`NodeExecutionContext`: `ProcessId`, `Node`, `Input` (вычисленные параметры), `Checkpoint`, `PreviousCheckpoint`
(снимок прошлой попытки — чтобы ключ от повтора совпадал во всех попытках, D-032), `Variables` (копия), `Attempt`,
`Time` (`TimeProvider` — блоки не используют `DateTime.Now` и `Task.Delay` без него), `Memory`, `Graph`, `EnteredFrom`,
`SecretValues` (чтобы не показать секрет на снимке экрана).

### 2.4. Итог шага (`NodeResult`)

Итоги:

| Вид | Значение |
|---|---|
| `Success(Port, VariableUpdates, Output, Message)` | подтверждено выполненным; `Memory`/`ReplaceMemory` — память блока; `ReleaseResources` — освобождаемые ресурсы |
| `RunAgain` | выполнить этот же блок снова с первой попытки (например, ресурс получен) |
| `Failure(Error, Retryable = true)` | подтверждено **не** выполненным. `ActionRan` — действие выполнилось, но идти некуда («Исправить и повторить» не предлагается); `MaybeExecuted` — могло выполниться, но повтор безопасен (ключ от повтора) |
| `Unknown(Reason)` | неизвестно, выполнилось ли; не повторяется автоматически. `AlreadyChecked` — блок уже сверился, сразу проверять бессмысленно |
| `Finish(Succeeded)` | конец процесса (блок «Завершение») |

Намерения — движок применяет их **в той же транзакции**, что и шаг (D-022): `Wait(Until)`, `WaitForEvent(Subscription,
TimeoutAt)`, `AcquireResource(Key, Title, TimeoutAt)` (`RepeatNode` — повторить блок после получения), `ReleaseResource`,
`Split(Ports)`, `Join`, `PublishEvent`, `StartChild(ScenarioId, Inputs, WaitForCompletion, TimeoutAt, SaveTo)`.
У любого итога может быть `Snapshot` — подсказка для разбора ошибки (на итог не влияет; секреты движок скрывает).

Помощники: `NodeResult.Ok(...)`, `NodeResult.Fail(msg, retryable)`. **`Fail` по умолчанию `retryable: true`** — для
окончательных отказов передавайте `false`.

`VerificationResult`: `Done` (может нести `Port`, `VariableUpdates`, `Output`, `ReleaseResources`), `NotDone`,
`Inconclusive`, `NotSupported`.

### 2.5. Проверка сценария и выражения

- `ScenarioValidator` (`src/Pepa.Core/Validation/`) — до любого запуска (в том числе дочернего и по автозапуску):
  коды `schema.version`, `start.missing`, `end.missing`, `edge.*`, `port.multiple`, `port.unconnected`, `field.*`,
  `node.config`, `settings.*`, `node.unreachable`, `node.no.exit`, `scenario.runTitle*`; `ParallelCheck` — правила
  участков «Параллельно»/«Дождаться всех ветвей» (`parallel.*`) и конфликты `SharedState`. `IsFieldVisible` — общее
  правило видимости полей (его же повторяет `ui/src/lib/graph.ts`).
- `JintExpressionEvaluator` — новый движок Jint на каждое вычисление, строгий режим, пределы: 2 с, 100 000 операторов,
  память, глубина рекурсии. Глобальные объекты: `vars`, `lastError`, `process`, у условий событий — `event`. Выражения
  без побочных эффектов: получают JSON-копии (D-009). Код JavaScript в браузере получает переменные данными (D-034).
- `TemplateParser` — `{{ … }}`; незакрытые и пустые `{{ }}` — ошибка.

### 2.6. Блоки движка и регистрация

`NodeRegistry` (`src/Pepa.Core/Nodes/NodeRegistry.cs`) — тип → исполнитель (повтор типа — исключение), разделы
палитры. Регистрация — одна цепочка в `PepaHost.StartCoreAsync`, затем `PluginLoader.Load` (расширения), и только потом
создаются `ProcessEngine` и `ScenarioAssistant`. После этого блоки не добавляются.

Разделы: `logic` «Логика», `flow` «События и процессы», `files`, `tables`, `web`, `browser`, `email`, `desktop`, `telegram`.

| Где | Блоки |
|---|---|
| `Engine/Nodes/LogicNodes.cs` и соседние | `start`, `end`, `condition`, `choice` (D-083), `variable.set`, `log`, `wait`, `text.find` |
| `Engine/Nodes/FlowNodes.cs` и соседние | `event.wait`, `event.send`, `resource.acquire`, `resource.release`, `scenario.call` (ждёт итог), `scenario.run` (не ждёт), `loop.forEach` (D-021), `parallel`, `join` (D-036), `user.ask` (D-060) |

«Для каждого» (`ForEachNode`) хранит список и позицию в памяти блока. Продолжает ли вход перебор, решает
`ForEachNode.Continues`: возврат из тела цикла — да; вход тем же путём, что и в первый раз, — новый перебор, только если до
этого пути можно дойти и после «Готово» (внешний цикл), иначе (пауза или проверка перед каждым элементом) — продолжение.
Движок спрашивает об этом до вычисления параметров: продолжение не вычисляет и не сохраняет список заново
(`InputResolver.Resolve(skipField: "items")`), поэтому хранилище растёт линейно от длины списка (D-021, D-090).
| адаптеры | раздел 5 |

## 3. Движок

`ProcessEngine` — partial-класс: `ProcessEngine.cs` (запуск, очередь, управление, `GetAllowedActions`),
`.Execution.cs` (шаг и фиксация итога), `.Platform.cs` (намерения, ожидания, ресурсы, дочерние процессы, сон),
`.Events.cs` (события и маршрутизатор), `.Triggers.cs`, `.Recovery.cs` (восстановление и решения), `.Questions.cs`,
`.Secrets.cs`.

### 3.1. Сущности и состояния

Записи — `src/Pepa.Engine/Persistence/Records.cs`:

- **Процесс** (`ProcessRecord`) — запуск сценария со **снимком** определения (`DefinitionJson`), переменными, памятью
  блоков, секретами, названием запуска (`Title`), входными данными без секретов (`Inputs`), `OriginKey`,
  `ParentProcessId`/`Depth`, состоянием отладки, счётчиком шагов и бюджетом. Правки сценария на идущий процесс не
  влияют (исключение — «Взять блок из сценария», 3.13).
- **Маркер** (`TokenRecord`) — где процесс: блок, `TokenState` (`Ready`, `Waiting`, `Completed`), `WaitKind`
  (`None`, `Join`, `Timer`, `Retry`, `Event`, `Resource`), `WakeAt`, `WaitRef`, `Attempt`, `Fork` (участок параллельных ветвей).
- **Шаг** (`StepRecord`) — одна попытка блока: вычисленные параметры (`Input`, с настоящими секретами), `Checkpoint`,
  `Output`, `Port`, ошибка, решение (`Resolution`). Состояние шага — главная защита от повторного выполнения.
- **Журнал** (`JournalEntry`), **событие** (`EventRecord`), **подписка** (`SubscriptionRecord`), **ресурс**
  (`ResourceRecord`) и **очередь** (`ResourceWaiterRecord`), **автозапуск** (`TriggerRecord`).

| Процесс (`ProcessStatus`) | Смысл |
|---|---|
| `Running`, `Waiting` | выполняется; ждёт (не занимает исполнителя) |
| `Paused` | пауза (`PauseReason`: `User`, `Restart`, `Breakpoint`) |
| `NeedsAttention` | ждёт решения (`AttentionKind`: `UnknownStep`, `StepFailed`, `StepLimit`, `EngineError`, `RestoredFromBackup`) |
| `Completed`, `Failed`, `Cancelled` | завершён |

Из `Paused` и `NeedsAttention` процесс выходит **только по действию пользователя**; какие действия возможны, решает
движок (`GetAllowedActions`), интерфейс показывает только их.

| Шаг (`StepState`) | Смысл |
|---|---|
| `Planned` | создан, действие не начато |
| `Running` | записано на диск **до** вызова внешнего действия |
| `Waiting` | ждёт срока, события, ресурса или вызванного сценария |
| `Succeeded` / `Failed` | подтверждён выполненным / подтверждён невыполненным (повтор безопасен) |
| `Unknown` | неизвестно, выполнилось ли (повтор вслепую запрещён) |
| `Skipped`, `Cancelled` | пропущен пользователем / отменён |

### 3.2. Запуск процесса

`StartProcessAsync` → `PrepareStartAsync`: сценарий из хранилища, `ScenarioValidator` (ошибки →
`ScenarioInvalidException` → HTTP 422), проверка типов входных данных → одна транзакция: процесс со снимком, маркер на
блоке «Начало». Название запуска — по шаблону `RunTitle` или по входным данным (D-081).

`OriginKey` уникален (`ux_processes_origin`), повторный запуск с тем же ключом возвращает существующий процесс:
`schedule:<триггер>:<срок>`, `event:<триггер>:<событие>`, `child:<родитель>:<шаг>`, `batch:<сценарий>:<ключ>:<i>`.

Несколько запусков (D-088): `StartProcessesAsync` — до `MaxBatchRuns` = 500, все наборы проверяются заранее,
процессы записываются одной транзакцией (все или ни одного), ключ пакета защищает от двойной отправки.

### 3.3. Планирование и исполнители

- Очередь — канал id процессов. `MaxParallelProcesses` исполнителей (`WorkerLoopAsync`); ожидающие процессы
  исполнителей не занимают.
- `ProcessSlot` — право изменять один процесс: у исполнителя или у управляющей операции. Пауза и остановка во время шага
  откладываются до границы шагов; остановка ещё и отменяет токен действия. Неудачная попытка взять право запоминается,
  и освобождающий ставит процесс в очередь снова — пробуждение не теряется (D-073). `Faulted` — хранилище отказало при
  записи ошибки движка: процесс не трогается до перезапуска.
- `RunSliceAsync`: до `StepsPerSlice` (50) шагов подряд, затем процесс уступает исполнителя. Маркеры выбираются по
  кругу; ожидания событий и ресурсов разрешаются `TryResolveWaitAsync`. `StepBudget` (по умолчанию 10 000) — защита
  от бесконечного цикла: `NeedsAttention` / `StepLimit`.
- `TimerLoopAsync` раз в `TimerInterval` (1 с) ставит в очередь процессы из `GetRunnableProcessIdsAsync`: только
  `Running`/`Waiting` с готовым маркером, наступившим сроком, исполненной подпиской или свободным ресурсом. `Paused` и
  `NeedsAttention` таймер не будит никогда. Он же замечает сон компьютера (3.15).
- Всё время — через внедрённый `TimeProvider` (в проверках — виртуальное время).

### 3.4. Как выполняется шаг

`ExecuteTokenAsync`:

1. Точка останова или пошаговый режим → `Paused`/`Breakpoint` (D-029).
2. `InputResolver.Resolve` вычисляет параметры; ошибка выражения или `NodeInputException` → неповторяемая ошибка
   (действие не выполнялось).
3. `RequiredResource(input)` не у процесса → служебный шаг с `AcquireResource { RepeatNode = true }`: ресурс занимается
   сразу или процесс встаёт в очередь; получив ресурс, блок выполняется снова.
4. `Effect = None` → `ExecutePureAsync`: `ExecuteAsync` без токена отмены; исключение → неповторяемая ошибка; итог —
   одной транзакцией. Такие блоки никогда не повторяются и не получают записи `Planned`/`Running`.
5. `ReadOnly`/`External` → `ExecuteActionAsync`:
   1. транзакция: шаг `Planned`, процесс `Running`, счётчик шагов +1;
   2. `PrepareAsync` (не дольше `DefaultActionTimeout`); сбой → ошибка (повторяемая, кроме `NodeInputException`);
   3. транзакция: шаг `Running` + время начала + `Checkpoint` — отметка «сейчас будет действие»;
   4. `ExecuteAsync` с тайм-аутом: `Settings.TimeoutSeconds`, иначе `ExpectedDuration + 30 с`, если это больше
      `DefaultActionTimeout` (2 мин), иначе `DefaultActionTimeout`. Тайм-аут или отмена → `Unknown`;
      `NodeInputException` → неповторяемая ошибка; любое другое исключение → `Unknown`. У `ReadOnly` `Unknown`
      превращается в `Failure`;
   5. `Unknown` без `AlreadyChecked` у блока с `SupportsVerification` → сразу `VerifyAsync` (не повтор). На второй и
      следующих попытках проверяется **самая ранняя** попытка этого посещения блока с неясным итогом
      (`GetUncertainAttemptsAsync`): `Done` → успех, `NotDone` → повторяемая ошибка (прежние попытки отмечаются как
      невыполненные), иначе `Unknown`;
   6. окончательная ошибка в посещении, где раньше была попытка с неясным итогом (`Unknown` или `MaybeExecuted`), —
      становится `Unknown` (`Output.uncertainStepId`): ветка «Ошибка» не выбирается так, будто ничего не было (D-064, D-065);
   7. `ApplyResultAsync` → `CommitResultAsync`.

### 3.5. Одна транзакция итога

`CommitResultAsync` — один `IStoreWrite`: шаг, переменные (проверка типов — кроме `External`), память блока, переход
маркера, состояние процесса, подписка, место в очереди, держатель ресурса, дочерний процесс, новое событие, журнал. Все
тексты проходят скрытие секретов. Намерения превращаются в записи в `ResolveIntentAsync`.

Переходы: `Success` → маркер по связи выхода (нет связи у обязательного выхода → ошибка движка); `RunAgain` → тот же
блок; `Wait` → маркер `Waiting/Timer`; `WaitForEvent` → подписка и `Waiting/Event`; `AcquireResource` (занят) →
место в очереди и `Waiting/Resource`; `Split`/`Join` — 3.11; `Finish` → `Completed`/`Failed`; `Failure` → повтор, ветка
«Ошибка» или `NeedsAttention/StepFailed`; `Unknown` → `NeedsAttention/UnknownStep`.

Работа, которая сама пишет в хранилище, собирается в `CommitEffects` и выполняется **после** фиксации
(`ApplyEffectsAsync`): постановка в очередь, уведомление интерфейса, возврат событий на сопоставление (`Rematch`),
поиск ранее пришедших (`Reclaim`), сигнал маршрутизатору, отмена дочерних процессов.

Завершение процесса (`OnTerminalAsync`, в той же транзакции): остальные маркеры завершаются, открытые шаги
отменяются, ресурсы освобождаются и передаются следующим, место в очередях снимается, подписки отменяются, вызванные
дочерние процессы останавливаются, назначенные, но не применённые события возвращаются на сопоставление, родителю
публикуется `pepa.process`/`finished`.

### 3.6. Ошибки и повторы

- `Failure` с `Retryable` и `Attempt < RetryCount + 1` (только `Effect != None`) → повтор: маркер `Ready` или
  `Waiting/Retry` через `RetryDelaySeconds`. Иначе — выход `error`, если подключён, иначе `NeedsAttention/StepFailed`.
- `Unknown` никогда не повторяется по настройкам блока — только решением пользователя.
- Выход `Optional` без связи («Тайм-аут», «Не найдена»): шаг считается невыполненным. Для `External` — с `ActionRan`
  (действие было), кроме выходов с `NothingDone`; то же для `scenario.call`. `ActionRan` скрывает «Исправить и повторить».
- Сбой хранилища во время шага → `TryMarkEngineErrorAsync` (шаги `Running` → `Unknown`, процесс
  `NeedsAttention`/`EngineError`); если и это не записалось — `slot.Faulted`, а наведёт порядок восстановление при
  следующем запуске.

### 3.7. Ожидания

Ожидание — это данные, а не поток: таймер (`Wait`), повтор с задержкой, событие (подписка), ресурс (очередь), слияние
ветвей. Все переживают перезапуск без изменений (оставшееся время сохраняется, процесс продолжит только после
решения пользователя). Блок «Ожидание» умеет ждать до времени суток (`untilTime`, местное время из `TimeProvider`).

### 3.8. События и сопоставление

**Приём.** `PublishEventAsync` (или намерение `PublishEvent`) записывает `EventRecord` в состоянии `Received`;
источник приводится к нижнему регистру. Повтор по паре (источник, `ExternalId`) не создаёт события
(`ux_events_external`) — возвращается `Duplicate`. Служебные источники `pepa.process` и `pepa.question` снаружи
публиковать нельзя.

| Источник | Кто создаёт | Ключ сопоставления |
|---|---|---|
| `manual`, `http`, любой свой | `POST /api/events` (интерфейс «Отправить событие», своя программа с токеном) | задаёт отправитель |
| `process` | блок `event.send` | задаёт блок |
| `pepa.process` | движок: завершение дочернего процесса (`finished`, `ExternalId` = `finished:<id>`) | id дочернего процесса |
| `pepa.question` | ответ на вопрос `user.ask` из интерфейса (`AnswerQuestionAsync`, мимо маршрутизатора) | уникальный на вопрос |
| `telegram` | `TelegramService` (виды `message`, `edited`) | чат (`user:…`, `chat:…`, `channel:…`) |
| `email` | `EmailService` (новое письмо) | нет (разделение — фильтром «Кому») |
| `files` | `FolderWatcher` (`created`, `changed`) | отслеживаемая папка |

`POST /api/events` принимает и источники адаптеров (`telegram`, `email`, `files`) — это возможность локальной программы
с токеном, а не ошибка.

**Маршрутизация.** `RouterLoopAsync` (по сигналу, запасной опрос раз в 5 с) под `_routingLock` берёт события по порядку
(`RouteEventAsync`) и ищет кандидатов (`FindCandidatesAsync`) среди действующих подписок: тот же источник; вид
подходит (`EventTypes.Accepts`: пустой вид — любой, **кроме** `edited`; список через `|`; D-077); точный ключ, если
задан; событие пришло во время ожидания или в разрешённое окно до него (с допуском 1 мин на часы источника);
процесс не завершён; условие (`MatchExpression`) даёт `true`.

- Один кандидат с наибольшим приоритетом → подписка `Active → Fulfilled`, событие `Received → Matched` (оба перехода
  условные), процесс — в очередь. Событие применяет сам процесс в своём шаге (D-016): выход `event` (+ `SaveTo`);
  событие → `Delivered`. Если шаг не удался — событие возвращается в `Unmatched` и на повторное сопоставление.
- Несколько равных, условие кандидата не вычислилось, или кандидата уже держит другой конфликт → `Conflict`: решает
  пользователь (`POST /api/events/{id}/resolve`), уведомление `ConflictRaised` (D-017, D-079).
- Никому → автозапуски по событию; если и их нет — `Unmatched`. Автозапуск запускается, только если событие новее
  запуска движка (по id и по `OccurredAt`) — либо пользователь нажал «Сопоставить снова» (`RerouteEventAsync`). Возврат
  событий на сопоставление (`Rematch`) автозапуски не запускает никогда (D-023).

**Окно «пришедшие раньше».** У подписки `LookbackSeconds` — окно блока **плюс** время от якоря `WaitAnchorAsync`: от
начала последнего внешнего действия перед ожиданием (вопроса) с первой его попытки, которая могла дойти до адресата.
Шаги без внешних действий окно не сдвигают; границы — прошлое посещение того же ожидания, разделение/слияние ветвей,
вызов или запуск сценария, отправка события (D-089). Новая подписка возвращает на сопоставление подходящие
необработанные события источника в окне (`Reclaim`); при запуске движка — для всех действующих ожиданий.

**Не взятые ответы.** `GET /api/processes/{id}/unmatched-events` (`ListUnmatchedForProcessAsync`) показывает
неподошедшие события, которые подходят ожиданию по источнику, ключу и условию; «Отдать этому процессу» —
`POST /api/events/{id}/resolve`.

**Невостребованные события.** Назначенное (`Matched`), но не применённое событие процесса, который завершился или
ожидание которого отменено (повтор, пропуск, остановка), возвращается на сопоставление (`ReturnMatchedEventsAsync`).

### 3.9. Тайм-ауты ожиданий

Срок ожидания — `WakeAt` маркера. Тайм-аут (подписка `Expired`, выход `timeout`, вызванный сценарий отменяется)
**откладывается**, пока:

- не прошло `CatchUpGrace` (1 мин) после запуска движка или пробуждения компьютера — для ожиданий, начатых раньше (D-033);
- источник догружает пропущенное (`SourceCatchingUp` … `SourceCaughtUp`), но не дольше `MaxCatchUpWait` (10 мин) после
  запуска, а при потере связи — после срока, начала «слепоты» или пробуждения, что позже (D-080);
- подписку держит нерешённый конфликт (D-079);
- есть принятое, но ещё не разобранное подходящее событие (D-089).

Ожидания `pepa.process` и `pepa.question` не откладываются. Ответ, пришедший после срока, не принимается: отложенный
тайм-аут не продлевает окно ожидания. О каждой отсрочке — одна запись в журнале процесса.

### 3.10. Ресурсы и очередь

Монопольный ресурс — строковый ключ (`adspower:<профиль>`, `pepa-browser:<профиль>`, `desktop:input`, свой у
`resource.acquire`). У ресурса не больше одного держателя: процесс или сама Pepa (`pepa:…`).

- Занять (`ResolveIntentAsync`): уже наш → дальше; свободен и нет активных ожидающих → занять; занят → проверка
  взаимной блокировки `WouldDeadlockAsync` (цепочка «кто кого ждёт», включая ожидание дочерних процессов): цикл →
  неповторяемая ошибка, иначе место в очереди (у маркера, а не у процесса) и `Waiting/Resource`.
- Освободить (блок, `Success.ReleaseResources`, завершение процесса, `POST /api/resources/release`) →
  `ReleaseAndGrantAsync`: ресурс получает первый в очереди, чей процесс `Running` или `Waiting`; приостановленные
  пропускаются, но сохраняют место (D-018).
- **Внешние держатели** — сама Pepa: `pepa:recording` (запись действий в окнах держит `desktop:input`,
  `TryHoldExternallyAsync`) и `pepa:locked` (компьютер заблокирован, D-086). `ClaimExternallyAsync` — заявка в памяти:
  если ресурс у процесса, после его освобождения он перейдёт к Pepa, а не к следующему в очереди. Снимается
  `ReleaseExternalAsync`. `StartupClaim` — заявка, применяемая в `StartAsync` после восстановления и до запуска
  исполнителей (компьютер заблокирован уже при запуске). После перезапуска ресурсы `pepa:` свободны.

### 3.11. Дочерние процессы и параллельные ветви

- `scenario.call`/`scenario.run` → `StartChild`: дочерний процесс создаётся в той же транзакции (`child:<родитель>:<шаг>`,
  глубина не больше 8). Ожидание итога — подписка на `pepa.process`/`finished` с ключом id дочернего процесса
  (`SubscriptionPurpose.ChildProcess`): выход `done` или ошибка с `ActionRan`. Остановка родителя останавливает
  вызванных детей (D-020). Секреты передаются дочернему процессу (D-055).
- «Параллельно» (`Split`) создаёт маркер на каждую ветвь с участком в `Fork` (`id:число ветвей`, вложенные — через `/`);
  «Дождаться всех ветвей» (`Join`) держит пришедшие маркеры (`WaitKind.Join`), пока не придут все, и продолжает одним
  (D-036). Шаги ветвей выполняет один исполнитель процесса по очереди; одновременно идут ожидания. Решение пользователя
  относится к остановившейся ветви (`processes.attention_token_id`), место в очереди ресурса — у ветви.

### 3.12. Автозапуск

`TriggerRecord`: `Schedule` или `Event`, `NextRunAt`, `MissedRuns`, `SkippedRuns`, `LastError`, `Overlap`
(`skip`/`allow`). Расписание без cron (D-019): `ScheduleCalculator`, режимы `interval`, `daily`, `once`.

- `TriggerLoopAsync` раз в секунду: опоздание больше `ScheduleLateGrace` (10 мин) → «пропущено» (не запускается
  само); `Overlap = skip` при работающем прошлом процессе → `SkippedRuns`; ошибка подготовки → `LastError` и
  `TriggerFailed` (уведомление, D-082).
- Пропущенное, пока Pepa была закрыта, — только счётчик (`RecordMissedSchedulesAsync` при запуске).
- По событию — из маршрутизатора (3.8), источники `pepa.question` запрещены.
- Цепочки (D-090): событие помнит процесс-отправитель (`EventRecord.SenderProcessId`; «Отправить событие» и событие о
  завершении вызванного сценария). `EventChainAsync` идёт вверх по цепочке (отправитель → его родитель или отправитель
  события, которым он запущен): сценарий автозапуска уже в цепочке → запуск не делается («запустил бы сам себя»,
  `LastError` и `TriggerFailed`); глубина больше 8 — тоже. Запущенный процесс получает `Depth` отправителя + 1. События
  извне начинают новую цепочку.

### 3.13. Решения пользователя

`ResumeAsync(id, action, variables, expectedRevision)` — под правом процесса (ждёт до 10 с), действие должно быть в
`GetAllowedActions`; `expectedRevision` — отметка состояния, которую видел пользователь: изменился процесс — отказ
(решения «для всех», D-088). Отказы — `EngineOperationException` (HTTP 409) с русским текстом.

| `ResumeAction` | Что делает |
|---|---|
| `Continue` | продолжить приостановленный (после `StepLimit` бюджет увеличивается на `DefaultStepBudget`) |
| `Verify` | `VerifyAsync` по шагу (или по ранней неясной попытке): выполнено → дальше; не выполнено → шаг снова |
| `Retry` | выполнить шаг ещё раз (следующая попытка, тот же ключ от повтора) |
| `MarkSucceeded`, `Skip`, `MarkFailed` | считать выполненным / пропустить / считать невыполненным (→ ветка «Ошибка») |
| `RunWithoutPauses` | выйти из пошагового режима |
| `RetryWithChanges` | «Исправить и повторить» (D-085): новые значения переменных (тип, правила секретов), тот же шаг с первой попытки; только при подтверждённой ошибке без `ActionRan` |
| `RetryWithNewBlock` | «Взять блок из сценария» (D-085): тот же блок (id и тип) из сохранённого сценария заменяет блок в снимке процесса |

Оба исправления отказывают, если шаг занял бы другой ресурс, пока процесс держит прежний.

### 3.14. Восстановление после перезапуска

`StartAsync`: запоминает последний id события и время запуска → `RecoverAsync` (ничего не выполняет):

- шаги `Running` → `Unknown` («приложение закрылось во время шага»), процесс → `NeedsAttention/UnknownStep`;
- шаги `Planned` → `Cancelled` (не начинались);
- остальные `Running`/`Waiting` → `Paused/Restart`; ожидания, подписки и очереди сохраняются;
- база восстановлена из копии (`RestoredFromBackupAt`, маркер `pepa_backup`) → каждый незавершённый процесс
  `NeedsAttention/RestoredFromBackup` (D-047); маркер снимает хост после `StartAsync`;
- `ReconcileResourcesAsync`: ресурсы завершённых процессов и держателей `pepa:` освобождаются, очереди чистятся;
- `RecordMissedSchedulesAsync`: пропущенные запуски — счётчик и новый срок;
- затем `StartupClaim`, исполнители, таймер, маршрутизатор, автозапуски. Принятые до закрытия события сопоставляются,
  но применяются процессами только после ручного продолжения.

Итог — `RecoveryReport` (показывается в `/api/info`, на «Главной» и в уведомлении).

### 3.15. Сон компьютера и догоняние источников

- `NoticeClock`: таймер не срабатывал дольше `SleepGap` (2 мин, задаёт хост) — компьютер спал или часы перевели;
  ожидания получают `CatchUpGrace`, событие `Woke(from, to)` — хост переподключает Telegram и почту (D-080).
- Источники, способные пропустить события (Telegram, почта, папки), вызывают `SourceCatchingUp(source, title, since)`
  до `StartAsync`, при потере связи и после сна, и `SourceCaughtUp` после догрузки.
- Компьютер не засыпает, пока есть работа (`PowerKeeper`), и будится к сроку (`WakeTimer`) — раздел 6.8.

### 3.16. Секреты

`ProcessEngine.Secrets.cs`: всё, что движок пишет текстом (сообщения, ошибки, решения, выходы, снимки, журнал),
проходит `Redact`; значения короче 3 символов не ищутся, короче 6 или из одних цифр — только целым словом.
`step.Input` и переменные хранят настоящие значения (нужны для проверки) и скрываются при показе (`SecretMasker`,
маска `••••`). `processes.secrets` и `events.secrets` переносят секреты между процессами и событиями (D-055).

### 3.17. Параметры (`EngineOptions`)

| Параметр | По умолчанию | Откуда |
|---|---|---|
| `MaxParallelProcesses` | 4 | «Настройки», после перезапуска |
| `DefaultStepBudget` | 10 000 | «Настройки», после перезапуска |
| `DefaultActionTimeout` | 2 мин | «Настройки» (`ActionTimeoutSeconds`), после перезапуска |
| `StepsPerSlice` / `TimerInterval` | 50 / 1 с | код |
| `ScheduleLateGrace` | 10 мин | код |
| `CatchUpGrace` / `MaxCatchUpWait` | 1 мин / 10 мин | код |
| `SleepGap` | null (в хосте — 2 мин) | `PlatformSettings.ToEngineOptions()` |
| `ShutdownGracePeriod` | 15 с | ожидание выполняющихся шагов при остановке |

## 4. Хранилище

### 4.1. SQLite

`Pepa.Storage.Sqlite` реализует `IPepaStore` (чтение без блокировки, своё соединение) и `IStoreWrite` (транзакция;
методы чтения «своих» записей; `CommitAsync`; освобождение без фиксации — откат). Один файл `pepa.db`: WAL,
`synchronous=FULL`, `foreign_keys=ON`, `busy_timeout`. Все записи — через один замок (`_writeLock`): одновременно
открыт один `IStoreWrite`, замок не повторно входимый (запись изнутри записи — взаимная блокировка). Порядок
замков: маршрутизатор → запись. Чтение через `_store` внутри открытой записи не видит её изменений — читать через
`IStoreWrite`. Без ORM (D-008).

Форматы: время — строка UTC фиксированного вида (сравнивается строками; только через `SqliteMapping.Date`);
перечисления — имена членов (переименование = миграция); `processes.debug` и `processes.secrets` — JSON с
настройками System.Text.Json по умолчанию (не `PepaJson`).

### 4.2. Схема и миграции

`SqliteStore.Schema.cs`: массив `Migrations`, только дописывается; номер миграции = `PRAGMA user_version` после неё.
Сейчас версия схемы **9**. База более новой версии не открывается («создана более новой версией Pepa»). Перед
обновлением схемы `SqliteStore.InitializeAsync` сам делает копию `backups/pepa-migration<N>-*.db`. Ещё раньше он
откладывает журнал прежней базы, если на место базы положили копию (база в режиме копии, рядом `pepa.db-wal`/`-shm`):
иначе SQLite применил бы журнал поверх копии (`SetAsideStaleJournal`, файлы `*.old`, D-090).

| № | Что добавила |
|---|---|
| 1 | `scenarios`, `processes`, `tokens`, `steps`, `journal`, `settings` |
| 2 | `events`, `subscriptions`, `resources`, `resource_waiters`, `triggers`; у процесса `parent_process_id`, `origin_key`, `depth`, `memory` |
| 3 | `processes.debug` (отладка) |
| 4 | `tokens.fork`, `processes.attention_token_id` (параллельные ветви) |
| 5 | место в очереди ресурса — у маркера (`resource_waiters` пересобрана) |
| 6 | `processes.secrets`, `events.secrets` |
| 7 | индекс `ix_subscriptions_event` (срок хранения событий) |
| 8 | `processes.title`, `processes.inputs` (D-081) |
| 9 | `events.sender_process_id` — процесс, отправивший событие (цепочки автозапуска, D-090) |

Ещё одна таблица — `pepa_backup` (только в резервной копии): по ней восстановленная база узнаётся при запуске.
Таблица `settings` — ключ/значение: `platform` (JSON `PlatformSettings`), `assistant.apiKey` (DPAPI),
`samples.version`, `samples.seeded`.

`SqliteWrite.UpdateProcessAsync` обновляет только изменяемые столбцы процесса (состояние, переменные, память,
секреты, счётчики…); `Title`, `Inputs`, определение, родитель, `OriginKey` записываются только при вставке
(определение — отдельно `UpdateProcessDefinitionAsync`).

### 4.3. Обслуживание

- `DatabaseBackups` (`src/Pepa.Host/Backups/`) — `backups/pepa-{daily,manual,migrationN}-yyyyMMdd-HHmmss.db` через
  `SqliteStore.BackupAsync` (онлайн-копия, `quick_check`, маркер `pepa_backup`, атомарное переименование). Хранится 7
  ежедневных, 10 ручных, 3 перед миграцией; ежедневная проверяется раз в час. Восстановление — вручную (заменить
  `pepa.db` копией, инструкция в «Настройках»); при запуске такая база переводит незавершённое в «ждёт решения» (D-047).
- `HistoryRetention` — раз в 6 ч: завершённые процессы старше `HistoryDays` (90; 0 — не удалять ничего) и их снимки;
  у обработанных событий — данные по тому же сроку (неподошедшие становятся `Ignored`), сами записи — не раньше чем
  через 365 дней (по ним узнаётся повторно пришедшее).
  Не удаляются незавершённые процессы, процессы с незавершённым родителем или потомком, держатели ресурсов (D-049, D-071).
- `SnapshotFolder` — снимки экрана при ошибке шага, `snapshots/<id процесса>/NNN.jpg|png`, не больше 20 на процесс и
  10 МБ на снимок (D-072).

Резервные копии содержат всё, что в базе, в том числе секретные переменные процессов (они в базе открыто, D-052) —
обращаться с `backups/` как с `pepa.db`. Ключи интеграций (помощник, почта, Telegram, AdsPower) зашифрованы DPAPI.

### 4.4. Папка данных

По умолчанию `%LOCALAPPDATA%\Pepa` (`HostPaths.DefaultDataDirectory`); оба приложения принимают `--data-dir`.

| Путь | Что | Кто пишет |
|---|---|---|
| `instance.lock` | блокировка экземпляра (pid, время) | `InstanceLock` |
| `pepa.db`, `-wal`, `-shm` | база | `SqliteStore` |
| `backups/` | резервные копии базы | `DatabaseBackups`, `SqliteStore` |
| `logs/pepa-yyyyMMdd.log` | журнал платформы (Information+, без ротации) | `FileLoggerProvider` |
| `logs/desktop-errors.log` | необработанные ошибки окна | `Pepa.Desktop` |
| `snapshots/<процесс>/` | снимки при ошибках шагов | `SnapshotFolder` |
| `watches/watch-<id>.json`, `watches/checked.json` | известные файлы каждой отслеживаемой папки; время последней проверки (прежний `<id>.json` переносится) | `FolderWatcher` |
| `telegram/session.dat`, `updates.json`, `account.json`, `proxy.json` | сессия MTProto, состояние обновлений, учётные данные (DPAPI), прокси (DPAPI) | `TelegramService` |
| `email/account.json`, `email/state.json` | ящик (пароль — DPAPI), последние UID | `EmailService` |
| `browser-sessions.json` | открытые браузеры процессов (профиль, вкладки, запуск) | `BrowserSessions` |
| `adspower-started.json` | какие профили (и какие запуски браузера) открыла Pepa | `BrowserSessions` |
| `browser-profiles/<имя>/` | профили браузера Pepa («Сохранять вход на сайты») | Playwright |
| `plugins/<id>/*.dll`, `plugins-data/<id>/` | расширения и их данные | пользователь / расширение |
| `desktop.json` | что делать при закрытии окна (`Ask`/`Tray`/`Exit`, числом) | `Pepa.Desktop` |
| `WebView2/` | профиль WebView2 | WebView2 |

Рабочая папка — отдельно: `PlatformSettings.WorkspaceRoot`, по умолчанию `Документы\Pepa Workspace` (нет «Документов» —
`<данные>\workspace`); `--workspace` у `pepa-server` действует, только пока она не задана.

## 5. Адаптеры

### 5.1. Общие правила

- Блок адаптера регистрируется методом `Register<X>Nodes(this NodeRegistry, …)`; долгоживущий сервис (соединения,
  файлы состояния) создаётся в `PepaHost` и передаётся блокам.
- `Effect` — честно. `Fail` — действие точно не выполнено (или повтор доказуемо безопасен — `MaybeExecuted`); всё, что
  могло выполниться, — `Unknown`. Проверка смотрит, а не повторяет.
- Ключи от повторов (Telegram `randomId`, HTTP `Idempotency-Key`, Message-ID письма) создаются в `PrepareAsync` и берутся
  из `PreviousCheckpoint` на следующих попытках. 64-битные числа в снимке — строками.
- Все пути — через `IWorkspace.Resolve`; имена извне — `Workspace.SafeFileName`; запись с проверкой содержимого —
  атомарная.
- Недоступная интеграция падает с понятной причиной, а не молчит и не ждёт вечно.
- После перезапуска адаптер только переподключается и сообщает; внешнее действие сам не повторяет.
- Источник событий: стабильный `ExternalId`, ключ сопоставления, `OccurredAt` по времени источника (найденное при
  догрузке — раньше `engine.StartedAt`, чтобы автозапуски не срабатывали); `SourceCatchingUp`/`SourceCaughtUp`.
- Блок ожидания возвращает `WaitForEvent` с выходами `event`, `timeout` (`Optional`), `error`; значения пользователя в
  условие — только через `JsonSerializer.Serialize`; заданный, но пустой после подстановки фильтр —
  `NodeInputException`, а не «подходит всё».

### 5.2. Файлы и рабочая папка (`Pepa.Adapters.FileSystem`)

- `Workspace.Resolve` — песочница: без выхода за корень, без символических ссылок и junction, без зарезервированных
  имён и точки/пробела в конце (D-014). Рабочая папка не может быть корнем диска, профилем, системной папкой или
  пересекаться с папкой данных (`SettingsService`).
- Блоки: `file.write`, `file.read`, `file.copy`, `file.move`, `file.delete`, `file.exists`, `file.list`, `folder.create`
  — у каждого `PrepareAsync` (наличие, размер, SHA-256) и `VerifyAsync`; запись через временные `.pepa-tmp-` файлы
  (`FileState`).
- Слежение за папками (D-051, D-053, D-054): `FolderWatcher` опрашивает до 20 папок (`PlatformSettings.FolderWatches`,
  меняются только через `PUT /api/watches`) раз в 2 с; файл — событие `files`/`created` или `changed`, когда перестал
  меняться и открывается. Файлы, бывшие до включения слежения, — не события; появившиеся, пока Pepa была закрыта, —
  события с `OccurredAt` до запуска. `file.wait` ждёт только отслеживаемую папку. Отключить слежение, от которого
  зависит ожидание или автозапуск, нельзя (409).

### 5.3. Таблицы (`Pepa.Adapters.Tables`, D-067, D-068)

`table.read`, `table.find` (выход «Не найдена» с `NothingDone`), `table.append`, `table.update` — .xlsx/.xlsm и
CSV/TSV в рабочей папке. Запись: временный файл + `File.Replace` с проверкой хеша снимка; правка ячеек одного листа
без потери остального; формулы не пишутся. Файл открыт в Excel/LibreOffice (файлы блокировки) — повторяемая ошибка
`TableBusyException`. Проверка записи — по снимку `PrepareAsync` (хеш, последняя строка). `.xls` не поддерживается.

### 5.4. HTTP (`Pepa.Adapters.Http`, D-058, D-061)

- `http.get` («Получить по адресу», `ReadOnly`) сам следует переадресациям, на другом домене убирает заголовки,
  https → http запрещён.
- `http.send` («Отправить запрос», `External`) не повторяется вслепую: ответ потерян, 5xx, 3xx кроме 307/308 —
  `Unknown`. С включённым `Idempotency-Key` (ключ один на все попытки) — `Failure { MaybeExecuted }`, повтор по настройкам.
- Общий `HttpClient`: без переадресаций и cookies, без своего тайм-аута (действует тайм-аут шага), соединение 30 с.
  В журнал адрес пишется без запроса и учётных данных.

### 5.5. Почта (`Pepa.Adapters.Email`, D-069, D-070, D-074)

- `EmailService` (MailKit) открывает папку IMAP только для чтения (`EXAMINE`, письма не помечаются прочитанными),
  проверяет раз в 30 с, новое письмо → событие `email` (`ExternalId` = `<ящик>:<UIDVALIDITY>:<UID>`). Письма, бывшие до
  настройки, — не события. Состояние — `email/state.json`, ящик — `email/account.json` (пароль DPAPI, назначение
  `Pepa.Email`). Нет связи — источник «слепой» (`Blind`), тайм-ауты ждут.
- `email.wait` — фильтры «От», «Тема», «Содержит», «Кому»; ключа сопоставления нет.
- `email.send` — SMTP; Message-ID создаётся в `PrepareAsync`. Отказ сервера → `EmailNotSentException` (не отправлено);
  обрыв после передачи → `EmailSendUncertainException` → `Unknown`; проверка ищет Message-ID в «Отправленных» и
  никогда не отвечает `NotDone` (не все серверы кладут туда письма).

### 5.6. Браузер и AdsPower (`Pepa.Adapters.Browser`, D-024, D-025, D-078, D-089)

**Блоки.** `browser.open`, `adspower.open`, `browser.goto`, `click`, `fill`, `press`, `waitFor`, `read`, `screenshot`,
`select`, `check`, `upload` (файл передаётся содержимым, D-027), `tab`, `waitUrl`, `evaluate`, `waitAny` (до 4 исходов,
исход должен держаться две проверки подряд, D-084), `browser.close`. Все действия со страницей — через `BrowserNodes.Run`:
сессия (с переподключением) → проверка потерянной вкладки и окон сайта между шагами → действие → разбор ошибок
Playwright → снимок при ошибке (если на странице не виден секрет). Нажатие: сначала проба без нажатия, затем нажатие;
тайм-аут нажатия — `Unknown` (форма могла уйти). Окна сайта (alert, confirm…) не закрываются молча (D-076).

**`BrowserSessions`** — один Playwright на приложение, сессии по (процесс, имя; обычно `main`). Записи сессий
(`BrowserSessionInfo`: профиль, ключ ресурса, `CloseOnFinish`, вкладка процесса `TargetId`, все вкладки процесса
`OwnedTargets`, порядок вкладок `TabOrder`, `OwnTab`, `Retired`, `Endpoint`) — в `browser-sessions.json`: после
перезапуска процесс продолжает в своей вкладке ещё открытого профиля.

- Ресурсы: `adspower:<id>` — один процесс на профиль; `pepa-browser:<профиль>` — только у `browser.open` с
  «Сохранять вход на сайты» (папка `browser-profiles/<имя>/`).
- **Свой или чужой профиль.** `adspower-started.json` — какие профили Pepa запустила сама. Профиль, открытый не Pepa,
  по умолчанию не трогается (`ifOpen = stop`); `ifOpen = tab` — работа в своей новой вкладке (`OwnTab`): процессу
  принадлежат только она и окна, открытые с неё, профиль в конце не закрывается, «Закрыть браузер» закрывает только
  вкладки процесса.
- **Идентичность запуска.** `Endpoint` — адрес отладки AdsPower с идентификатором браузера: если профиль закрыли без
  Pepa и открыли заново, это «открыт заново не Pepa» — процесс в нём не работает и в конце его не закрывает (D-089).
- **«Прежний N».** Процесс открыл под тем же именем сессии другой профиль, не закрыв прежний: прежний живёт до конца
  процесса под именем «main (прежний 1)», отключён от Playwright и закрывается в конце, если так задано.
- Сессия записывается **до** запуска профиля; запуску дано до 5 мин (`ExpectedDuration`); «ещё открывается» и
  тайм-аут — `Unknown`, проверка смотрит, открыт ли профиль, а не открывает снова.
- Закрытие в конце: `engine.ProcessFinished` → `BrowserSessions.ReleaseProcessAsync` (подписка — в `PepaHost`);
  неудача → `ReleaseProblem` → запись в журнале процесса. При запуске `Prune` забывает сессии завершённых процессов,
  ничего не закрывая.

**`AdsPowerClient`** — Local API v2 (на 404 — v1), ключ `Authorization: Bearer`, запросы строго по одному с паузой
1100 мс, тайм-аут 90 с; ответы классифицируются по ключевым словам (`Explain`) с русским пояснением.

**Запись действий** (D-030): `BrowserRecorder` в профиле Pepa `main` или в профиле AdsPower; пароли не записываются;
превращение в блоки — в интерфейсе (`ui/src/lib/recording.ts`). Ресурс профиля проверяется только при старте записи.

### 5.7. Telegram (`Pepa.Adapters.Telegram`, D-026, D-033, D-037, D-038, D-077, D-089)

- `TelegramService` (WTelegramClient, MTProto, свой аккаунт): вход по api_id/api_hash, телефону и коду; прокси
  SOCKS5 (свой код) и MTProxy; файлы `telegram/` (учётные данные и прокси — DPAPI, `Pepa.Telegram`). Входящие
  сообщения и изменения → события `telegram` (`message`/`edited`, ключ — чат; `ExternalId` с отпечатком изменения).
  Время событий — по часам компьютера с поправкой на сервер.
- `ITelegramGateway` — то, что видят блоки (в проверках — `FakeTelegram`).
- `telegram.send` — `randomId` и `afterId` (последнее сообщение чата) в `PrepareAsync`; проверка ищет сообщение в
  истории после `afterId`. `RANDOM_ID_DUPLICATE` → уже отправлено (успех). Отправка неоднозначна, если за время запроса
  был `-503` или переподключение: `TelegramSendUncertainException` → `Unknown { AlreadyChecked }`, без ветки «Ошибка» и
  без «Исправить и повторить» — повтор только с тем же ключом.
- `telegram.wait` — сравнение текста, «ответ на сообщение», «от кого» (D-077); `telegram.button` — проверяемы только
  кнопки клавиатуры (текст); `telegram.download` — атомарно, проверка `NotDone`, пока вложение доступно.
- Догрузка после запуска, потери связи и сна — `WillCatchUp`/`Blind`/`CaughtUp` → движок (3.9).

### 5.8. Окна Windows (`Pepa.Adapters.Desktop` + `.Windows`, D-028, D-031, D-044, D-045)

- `IDesktop` — одна попытка без ожиданий; ожидания окна и элемента — в блоках (`WaitWindowAsync`, по реальным часам).
  Блоки: `desktop.start`, `window`, `click`, `fill`, `read`, `waitFor`, `keys`, `mouse`, `close`.
- Мышь и клавиатура — общий ресурс `desktop:input` (блоки ввода объявляют его в `RequiredResource`). Компьютер
  заблокирован — ресурс у `pepa:locked` (3.10).
- `UiaDesktop` — FlaUI UIA3 на своём потоке; ввод только в окно, которое действительно впереди и не выше по уровню
  целостности; `Invoke` ждёт 3 с (дальше — `Pending` → `Unknown`); частичный ввод — `Unknown`. Снимок окна при ошибке —
  только если у процесса нет секретов.
- Запись (`DesktopRecorder` + `IDesktopInputMonitor`, реализация `UiaInputMonitor`) держит `desktop:input` как
  `pepa:recording`; шаги собираются в C# (`DesktopRecording.cs`), пароли — секретными переменными.
- Создаётся только в `Pepa.Desktop/App.xaml.cs`; в `pepa-server` — `UnavailableDesktop` (блоки падают с причиной).

### 5.9. ИИ-помощник (`Pepa.Assistant`, D-035, D-041)

`ScenarioAssistant`: каталог блоков строится из `NodeRegistry` **один раз** при создании (поэтому все блоки — до него);
`ScenarioPrompt` — правила по блокам + каталог + два примера (`SampleScenarios.TelegramReply`, `AdsPowerTelegram`);
структурированный ответ → `DraftConverter` → `ScenarioValidator`, до 2 попыток исправления (`MaxRepairs`). Умеет
составить сценарий, изменить текущий (`AssistantRequest.Current`, изменения показываются до применения) и объяснить.
Ничего не запускает; при тайм-ауте не повторяет. Ключ — `AssistantKeyStore` (6.6); адрес API подменяется
`PepaHostOptions.AssistantBaseUrl` (проверки, e2e).

## 6. Хост (`Pepa.Host`)

### 6.1. Композиция (`PepaHost`)

`PepaHost.StartAsync(PepaHostOptions)` → `StartCoreAsync`, порядок важен:

1. Полный путь папки данных, `InstanceLock.Acquire` (D-013). При сбое дальше освобождается только блокировка.
2. `SqliteStore.InitializeAsync` (миграции, копия перед ними, маркер восстановления).
3. `SettingsService.LoadAsync` (при первом запуске сразу записывает рабочую папку).
4. Адаптеры: `Workspace` и `AdsPowerClient` (читают `settings.Current` через замыкание — изменения действуют сразу),
   `SnapshotFolder`, `BrowserSessions`, `TelegramService` и `EmailService` (публикуют в движок через отложенную ссылку
   `engineRef`), `IDesktop` (`options.Desktop` или `UnavailableDesktop`).
5. Цепочка регистрации блоков → `PluginLoader.Load`.
6. Токен (`options.Token` или 24 случайных байта), `HostContext`, `WebApplication.CreateSlimBuilder`, журнал в файл
   (и консоль), Kestrel на `IPAddress.Loopback`, JSON как `PepaJson`.
7. Синглтоны DI: экземпляры (хранилище, реестр, адаптеры, записи действий, `AssistantKeyStore`) и фабрики
   (`EventHub`, `AttentionNotifier`, `HistoryRetention`, `DatabaseBackups`, `FolderWatcher`, `ProcessEngine` с
   `settings.Applied.ToEngineOptions()`, `ScenarioService`, `PowerKeeper`, `WakeTimer`, `ScenarioAssistant`, `ProcessMapper`).
8. После `Build`: `engineRef = engine`; подписки (6.4: SSE; уведомления; питание; `ProcessFinished` → браузеры;
   `SourceCatchingUp`/`Blind`/`CaughtUp`/`Woke`); `engine.RestoredFromBackupAt`; `StartupClaim`, если компьютер
   заблокирован.
9. `engine.StartAsync` — **восстановление до всего остального**; затем снятие маркера копии, `browsers.Prune`,
   `notifier.Start`, Telegram — в фоне, примеры (`SeedSamplesAsync`).
10. Конвейер: `LocalAccessMiddleware` → для `/api` `ApiEndpoints.HandleErrorsAsync` → `MapPepaApi` →
    `/api/{**rest}` (JSON 404) → интерфейс (`MapInterface`).
11. `app.StartAsync`, затем `DatabaseBackups.Start`, `HistoryRetention.Start`, `watcher.Start`, `email.Start`
    (источники событий — только после запуска движка), фактический порт → `HostContext.Port`.

Остановка `StopAsync` — однократная (повторный вызов получает ту же задачу), в пуле потоков: `telegram.BeginShutdown`
→ параллельно (папки и почта → `engine.StopAsync`, ждёт шаги до 15 с) и остановка Kestrel (5 с; `/api/stream`
закрывается по `ApplicationStopping`) → Telegram, записи действий, копии, срок хранения, уведомления, `BrowserSessions`
(дожидается закрытия браузеров завершившихся процессов; профили незавершённых остаются открытыми) → `AdsPowerClient` →
`EventHub` → контейнер (фабричные синглтоны, в том числе `PowerKeeper`/`WakeTimer`) → `SqliteConnection.ClearAllPools` →
блокировка. Экземпляры, добавленные `AddSingleton(instance)`, контейнер не освобождает — это делает `StopCoreAsync`.

Обработчики событий движка вызываются синхронно в его потоках: они должны быть быстрыми и не бросать исключений
(у `EventsChanged` и `TriggersChanged` исключение вернётся вызывающему уже после фиксации).

### 6.2. Локальный API и безопасность

- Kestrel слушает только `127.0.0.1`. `Pepa.exe` — случайный порт и случайный токен при каждом запуске; `pepa-server`
  принимает `--port` и `--token` (по умолчанию тоже любой свободный порт и случайный токен).
- `LocalAccessMiddleware`: заголовок `Host` — `127.0.0.1` или `localhost` с нашим портом (защита от DNS rebinding),
  иначе 400 `Invalid host`; для `/api` — заголовок `X-Pepa-Token` (сравнение за постоянное время), иначе 401
  `{ "error": "Нет доступа: неверный токен. Откройте Pepa заново." }`. Ответы получают `X-Frame-Options: DENY`,
  `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`. CORS нет и не должно быть: защита от чужих сайтов —
  нестандартный заголовок.
- Токен передаётся интерфейсу только во фрагменте адреса (`LaunchUrl` = `…/#token=…`), который не уходит на сервер;
  `pepa-server` печатает эту ссылку в консоль, страница «Интеграции» показывает пример `curl` с токеном.
- API не возвращает секреты: ключ AdsPower — только `adsPowerHasKey`, ключ помощника — подсказка из 4 последних
  символов, пароли почты и Telegram, api_hash, секреты прокси — признаки `has…`. Всё, что пришло из процессов, событий,
  вопросов и автозапусков, проходит `ProcessMapper`/`SecretMasker`/`ProcessEngine.Redact`; секретные входные данные
  автозапуска показываются `••••`, и `••••` при сохранении значит «оставить прежнее».

### 6.3. Контракт ошибок

Ответ с ошибкой — `ErrorDto { error, validation }` с русским текстом; интерфейс показывает `error` как есть.

| Исключение | Код |
|---|---|
| `ScenarioInvalidException` | 422 (+ `validation` — отчёт проверки) |
| `EngineOperationException`, `ConcurrencyException` | 409 |
| `SettingsValidationException`, `BadHttpRequestException`, `JsonException` (внутри обработчика) | 400 |
| «не найдено» в обработчиках | 404 с `ErrorDto` |
| неизвестный адрес под `/api` (и неверный метод существующего адреса) | 404 «Такого адреса API нет.» |
| прочее | пустой 500 (в журнале платформы) |

Ошибка разбора тела запроса самим ASP.NET Core даёт 400 без `ErrorDto`; ошибки адаптеров обработчики ловят сами и
отвечают 409/502 с `ErrorDto`.

### 6.4. Поток изменений (SSE)

`GET /api/stream`: `retry: 2000`, затем строки `data: {"type":…,"id":…}`, комментарий `: ping` каждые 15 с; поток
завершается при остановке Pepa. `EventHub.Publish(type, id)` не блокирует; одинаковые пары (тип, id) склеиваются и
отправляются раз в 150 мс; у подписчика буфер 1000 сообщений, старые отбрасываются. Сообщение — только сигнал
«перечитай»: данных в нём нет.

| Тип | Кто публикует |
|---|---|
| `process` (id) | `engine.Changed` |
| `events` / `triggers` | `engine.EventsChanged` / `engine.TriggersChanged` |
| `overview` | `PowerKeeper.Changed`, `WakeTimer.Changed`, пробуждение (`Woke`) |
| `resources` | `POST /api/resources/release`, начало и конец записи действий в окнах (она держит `desktop:input`) |
| `recorder` | `BrowserRecorder.Changed`, `DesktopRecorder.Changed` |
| `watches` | `FolderWatcher.Changed` |
| `integrations` | `StatusChanged` Telegram и почты |
| `scenarios`, `scenario` (id) | создание, сохранение, копия и удаление сценария через API |

### 6.5. Адреса API

Все — под `/api`, с токеном. Карта — `ApiEndpoints.MapPepaApi`; ответы — записи `Dtos.cs` (их зеркало —
`ui/src/api/types.ts`, ведётся вручную).

| Группа | Адреса |
|---|---|
| Система | `GET /info` (версия, папки, `recovery`), `GET /node-types` (разделы и описания блоков), `GET /overview` («Главная»: счётчики, ждущие решения, питание), `GET /stream`, `GET /plugins`, `GET /templates` (примеры как шаблоны), `POST /notifications/test`, `GET /backups`, `POST /backups` (копия сейчас) |
| Настройки | `GET /settings`, `PUT /settings`, `GET /watches`, `PUT /watches` (слежение за папками) |
| Сценарии | `GET /scenarios`, `POST /scenarios`, `GET /scenarios/{id}`, `PUT /scenarios/{id}` (`expectedVersion`), `DELETE /scenarios/{id}`, `POST /scenarios/{id}/duplicate`, `POST /scenarios/validate`, `POST /scenarios/{id}/run` (`inputs`, `stepMode`, `runId` — ключ запуска: тот же ключ — тот же процесс), `POST /scenarios/{id}/run-many` (`runs`, `batchId`) |
| Процессы | `GET /processes?view=&scenarioId=&limit=`, `GET /processes/{id}`, `POST /processes/{id}/pause`, `POST /processes/{id}/cancel`, `POST /processes/{id}/resume` (`action`, `variables`, `expectedRevision`), `GET /processes/{id}/snapshots/{name}`, `GET /processes/{id}/unmatched-events` |
| Вопросы | `GET /questions?processId=`, `POST /questions/{id}/answer`, `GET /questions/{id}/image` (картинка из рабочей папки) |
| События | `GET /events?view=&limit=`, `POST /events`, `POST /events/{id}/resolve` (`subscriptionId`), `POST /events/{id}/reroute` |
| Ресурсы | `GET /resources`, `POST /resources/release` (`key`) |
| Автозапуск | `GET /triggers?scenarioId=`, `POST /triggers`, `PUT /triggers/{id}`, `DELETE /triggers/{id}`, `POST /triggers/{id}/run`, `POST /triggers/{id}/dismiss` (просмотрены пропущенные) |
| AdsPower | `POST /integrations/adspower/check`, `GET /integrations/adspower/profiles` |
| Почта | `GET /integrations/email`, `PUT /integrations/email`, `DELETE /integrations/email`, `POST /integrations/email/check` |
| Telegram | `GET /integrations/telegram`, `POST /integrations/telegram/login`, `POST /integrations/telegram/login/continue`, `POST /integrations/telegram/logout`, `PUT /integrations/telegram/proxy`, `POST /integrations/telegram/proxy/check` |
| Окна Windows | `GET /integrations/desktop/windows`, `GET /integrations/desktop/elements?window=&process=` (409, если недоступно) |
| Запись действий | `GET /recorder`, `POST /recorder/start`, `POST /recorder/stop`, `GET /recorder/desktop`, `POST /recorder/desktop/start`, `POST /recorder/desktop/stop` |
| ИИ-помощник | `GET /assistant`, `PUT /assistant/key`, `DELETE /assistant/key`, `POST /assistant/check`, `POST /assistant/draft`, `POST /assistant/explain` |

Значения `view`: у процессов — `active`, `finished`, `attention`, `working` (`Running`, `Waiting`, `Paused`), иное —
все; лимит по умолчанию 200 (до 1000), у `attention` — без обрезки (до 100 000), чтобы пакет запусков не вытеснил
остановку. У событий — `open` (`Received`, `Matched`, `Conflict`), `conflict`, `unmatched`, иное — все.

`POST /api/events` — способ передать событие из своей программы на этом компьютере (с тем же токеном): `source`,
`type`, `correlationKey`, `externalId` (ключ от повторов), `payload`; ответ — `eventId` и `duplicate`.

### 6.6. Настройки и секреты

- `PlatformSettings` (`Settings/SettingsService.cs`) — один JSON в `settings.platform`: `WorkspaceRoot`,
  `MaxParallelProcesses`, `StepBudget`, `ActionTimeoutSeconds`, `AdsPowerUrl`, `AdsPowerApiKey`, `NotifyTelegram`,
  `KeepAwake`, `WakeForSchedule`, `HistoryDays`, `FolderWatches`. `Current` — действующие (читаются через замыкания),
  `Applied` — с которыми стартовал движок; параметры движка применяются после перезапуска (`RestartRequired`).
- `PUT /api/settings` собирает **новый** `PlatformSettings` явным перечислением полей (рабочая папка и параметры движка —
  из запроса, остальные — `request.X ?? current.X`, `null` = «не менять»): забытое поле сбрасывается при каждом
  сохранении. `FolderWatches` переносятся как есть — меняются только через `PUT /api/watches`.
- Секреты хранятся DPAPI (`Pepa.Core.Security.Secrets`, текущий пользователь, назначение: `Pepa.Assistant`,
  `Pepa.Email`, `Pepa.Telegram`; вне Windows — открытым текстом). `AssistantKeyStore` — ключ Anthropic в
  `settings.assistant.apiKey`; значение, которое не расшифровалось (другой компьютер), считается «не задано».
  Ключ AdsPower — внутри `platform`, зашифрованный (`Pepa.AdsPower`); в памяти `PlatformSettings` он расшифрован,
  прежний открытый ключ шифруется при загрузке (D-090). Сохранённый пароль почты подставляется, только если не
  менялись сервер чтения, сервер отправки и логин.

### 6.7. Уведомления (`AttentionNotifier`, D-048)

Мигание окна или сообщение у значка (`IDesktopAlerts`) и, если включено, сообщение в «Избранное» Telegram: процесс
ждёт решения, новый вопрос (D-060), конфликт события (D-079), сбой автозапуска (D-082), итог восстановления после
запуска (ждёт подключения Telegram до 5 мин). Пакетами по 5 с; каждая остановка (по сочетанию вида, шага и текста) и
каждый вопрос — один раз; известное до запуска не повторяется; отправка не повторяется, сбой только пишется в
журнал. Тексты — только из уже скрытых источников, без данных событий. `POST /api/notifications/test` — проверка.

### 6.8. Питание

- `PowerKeeper` (D-080): пока есть процессы `Running`/`Waiting` и включено `KeepAwake`, свой поток держит
  `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` (экран гаснет как обычно). Пересчёт по
  `engine.Changed` (склейка 1 с) и раз в минуту.
- `WakeTimer` (D-087): таймер пробуждения Windows на ближайший будущий срок — включённое расписание или
  `WakeAt` ждущего/выполняющегося процесса (приостановленные не в счёт) — за 2 мин до срока; после пробуждения держит
  компьютер 5 мин. Пересчёт при изменениях процессов и автозапусков, сохранении настроек и раз в 5 мин.
  `IWakePlatform`: `WindowsWakePlatform`, `UnsupportedWakePlatform` (в проверках — подделка).
- Оба состояния показываются на «Главной» (`PowerDto` в `/api/overview`). На Linux не поддерживаются.

### 6.9. Расширения (`Plugins/PluginLoader.cs`, D-043)

Каждая подпапка `plugins/<id>/` — сборки .NET, каждая в своём `AssemblyLoadContext` (сборки Pepa и .NET — общие).
Класс с `IPepaPlugin` регистрирует разделы и блоки через `PluginRegistration` (`AddCategory`, `AddNode`,
`DataDirectory` = `plugins-data/<id>/`, `WorkspaceRoot`, `ResolveWorkspacePath`). Расширение принимается целиком или
отвергается с причиной: тип с точкой и своим префиксом (не из `ReservedPrefixes`), без повторов, без начала/конца и
параллельных блоков, `SupportsVerification` — только с `External`, заполненные тексты, существующий или свой раздел,
уникальные выходы и поля. Блок оборачивается в `PluginNode`: описание — копия, проверенная при загрузке (читается один
раз), сбой `Validate` расширения — замечание проверки, а не её падение; исключения при выполнении движок разбирает как у
любого блока. При запуске с правами администратора расширения не загружаются. Итог — `PluginCatalog`
(`GET /api/plugins`, «Настройки» → «Расширения»). Образец — `samples/Pepa.Plugins.Sample` (его README — правила для
авторов).

`ReservedPrefixes` сейчас: `browser.`, `adspower.`, `telegram.`, `desktop.`, `file.`, `folder.`, `event.`, `resource.`,
`scenario.`, `loop.`, `text.`, `variable.`, `test.`, `email.`, `table.`, `http.`, `user.`. Тест
`Every_built_in_block_prefix_is_reserved_from_extensions` сверяет список со всеми встроенными блоками: новый встроенный
адаптер обязан добавить свой префикс.

### 6.10. Примеры и прочее

- `SampleScenarios` — 16 примеров, набор версионирован (`CurrentVersion`, `Versioned()`): при обновлении добавляются
  только новые (ключ `samples.version`), удалённые пользователем не возвращаются. Каждый пример должен проходить
  проверку без ошибок и предупреждений (проверка `ApiTests`). Они же — `GET /api/templates` и примеры для помощника.
- `FileLoggerProvider` — `logs/pepa-yyyyMMdd.log`, уровень Information (Microsoft.* — от Warning), без ротации;
  секреты, заголовки и строки запроса в журнал не пишутся.
- `InstanceLock` — эксклюзивно открытый `instance.lock`; второй экземпляр получает `InstanceAlreadyRunningException`.

### 6.11. `pepa-server` и `Pepa.exe`

| | `pepa-server` (`Pepa.Server/Program.cs`) | `Pepa.exe` (`Pepa.Desktop`) |
|---|---|---|
| интерфейс | обычный браузер по ссылке из консоли | WebView2 в окне |
| порт, токен | `--port`, `--token` или случайные | случайные |
| другие ключи | `--data-dir`, `--workspace`, `--ui`, `--no-samples`, `--browser`, `--assistant-url` | `--data-dir`, `--minimized` (автозапуск и перезапуск), `--after <pid>` |
| окна Windows, запись в окнах | недоступны (`UnavailableDesktop`) | `UiaDesktop`, `UiaInputMonitor` |
| второй экземпляр | код выхода 2 (неизвестный ключ — 1) | активирует окно первого (`Local\Pepa.Activate.<хеш папки>`) |
| остановка | Ctrl+C / SIGTERM → `StopAsync` | меню трея, крестик, завершение сеанса |

### 6.12. Окно Windows (`Pepa.Desktop`, D-003, D-086)

- `App.xaml.cs`: `ShutdownMode = OnExplicitShutdown`; первым подключается `SessionEnd`; хост стартует в пуле потоков
  (`IsDesktop`, `Desktop`, `DesktopInput`, `DesktopAlerts = WindowAlerts`, `IsSessionLocked = SessionLock.IsLocked`);
  `RegisterApplicationRestart` с `--minimized`; `SystemEvents.SessionSwitch` → блокировка/разблокировка:
  `ClaimExternallyAsync`/`ReleaseExternalAsync(desktop:input, pepa:locked)`.
- `MainWindow`: WebView2 с профилем в `<данные>/WebView2`, открывает `LaunchUrl`; чужие адреса — во внешнем браузере
  (`IsOwnPage` → `Pepa.Host.Security.OwnPage`: точное совпадение схемы, узла и порта). Мост `chrome.webview.postMessage` —
  только `pickFolder` и `openFolder` (только существующие локальные папки, сетевые пути `\\сервер\…` — нет),
  ответ `{id, ok, result|error}`; всё остальное — через обычный API (D-004).
- Закрытие: `OnClosing` всегда отменяет закрытие, пока оно не разрешено; вопросы задаются после. Выбор «свернуть в
  трей / закрыть» — `desktop.json`. `TrayIcon`: «Открыть Pepa», «При закрытии окна — сворачивать сюда», «Запускать
  вместе с Windows», «Выйти из Pepa»; подсказка — счётчики (`PepaHost.CountsAsync`).
- Автозапуск: `Autostart` (HKCU `…\Run` + `StartupApproved`), имена и командная строка — `AutostartEntry` (своя запись
  на каждую папку данных).
- Завершение сеанса: останавливается только на `WM_ENDSESSION` (не на `WM_QUERYENDSESSION` — завершение могут отменить),
  ждёт остановку до 4 с (не успевшие шаги станут «неизвестно» при следующем запуске). Restart Manager — обычный выход
  без вопросов (окна-вопросы с заголовком «Pepa» получают «Нет»).
- Проверяется только вручную (CHECKLIST.md).

## 7. Интерфейс (`ui/`)

### 7.1. Устройство

React 19 + TypeScript (strict), Vite, без библиотеки маршрутизации и без глобального хранилища. Одно и то же
приложение работает в WebView2 и в браузере против `pepa-server`.

| Папка | Что |
|---|---|
| `src/main.tsx` | `initToken()`, тема, `QueryClient` (staleTime 2 с, до 2 повторов, не на 404, перечитывание при фокусе) |
| `src/App.tsx` | `TokenGate`, оболочка с меню, `startEventStream`, выбор страницы по маршруту |
| `src/router.ts` | маршруты по фрагменту `#/…` (`Route`, `parseRoute`, `href`, `navigate`, защита несохранённых правок) |
| `src/api/` | `client.ts` (единственное место с адресами `/api`, токен, `ApiError`), `types.ts` (зеркало `Dtos.cs`), `hooks.ts` (запросы TanStack Query и их ключи), `events.ts` (SSE) |
| `src/pages/` | «Главная», «Сценарии», «Выполнение» (`RunsPage`), «История», страница процесса, «Интеграции», «Настройки» |
| `src/pages/editor/` | конструктор: `EditorPage`, `useEditor` (состояние и отмена), `Palette`, `NodeCard`, `GroupNodes`, `Inspector`, `FieldEditor`, `IssuesPanel`, `AssistantDialog`, `RecorderDialog`, `ElementPicker` |
| `src/components/` | общие: `ui.tsx` (Button, Card, Callout…), `feedback.tsx` (toast, confirm), `process.tsx`, `RunDialog`, `FixAndRetryDialog`, `groupActions`, `selection`, `questions`, `platform` (события, ресурсы), `triggers`, карточки интеграций, `Icon.tsx` |
| `src/lib/` | логика без React (под тесты vitest): `graph`, `groups`, `clipboard`, `recording`, `assistant`, `runMany`, `groupActions`, `status`, `format`, `fixRetry`, `export`, `report`, `desktop`, `theme`, `native` |
| `src/i18n/ru.ts` | общие строки и подписи перечислений |
| `src/styles/app.css` | токены дизайна, темы (тёмная — в двух блоках), `--cat-*` цвета разделов |

### 7.2. Данные: REST + SSE как сигнал

- Все данные — из REST через `api.*` и хуки `hooks.ts`. SSE (`events.ts`) читается через `fetch` (EventSource не умеет
  передать `X-Pepa-Token`) и только инвалидирует ключи запросов по типу сообщения (таблица 6.4, `keysFor`); после
  переподключения (пауза от 1 до 15 с) инвалидируется всё. Плюс периодическое перечитывание (обзор 15 с, процессы 10 с…).
- Инвалидации склеиваются (`createInvalidator`, D-090): каждый ключ — не чаще раза в 200 мс и не раньше, чем закончится
  его прежний запрос (`cancelRefetch: false`; после него ключ перечитывается ещё раз). Без этого при работающих
  процессах медленные списки не обновлялись бы вовсе, а брошенные запросы занимали бы соединения.
- Каждый тип, который публикует сервер, должен иметь ветку в `events.ts` — иначе часть экрана устареет до опроса.
- Решения: кнопки — только из `allowedActions` процесса; рискованные — через `feedback.confirm`; все решения «продолжить»
  (по одному процессу, «Исправить и повторить», выбранные, групповые) передают `expectedRevision` того состояния, что
  видел пользователь. Запуск (`useRunScenario`) не отправляется второй раз, пока идёт первый, и несёт `runId`.
  Интерфейс сам ничего не продолжает и не повторяет.
- Ошибки — `ApiError` с текстом сервера (или «Нет связи с движком Pepa…»), показываются как есть.
- Секреты и в интерфейсе не повторяются: поля паролей пустые («пусто — без изменений»), экспорт сценария очищает
  значения секретных переменных (`lib/export.ts`, D-075), отчёт о процессе — без значений переменных (`lib/report.ts`).

### 7.3. Маршрутизация и токен

Маршрут — фрагмент `#/…` (работает в WebView2 и браузере без настройки сервера): `home`, `scenarios`, `editor/{id}`,
`runs`, `history?scenario=`, `process/{id}`, `integrations`, `settings`. `initToken()` берёт `#token=…`, кладёт в
`sessionStorage` (`pepa.token`) и убирает из адреса; без токена — `TokenGate` (ввод вручную). Новый токен сервера
(перезапуск) — 401 до повторного открытия ссылки. Картинки с токеном загружаются в blob-адреса (`imageUrl`).

### 7.4. Тексты

Интерфейс на русском (D-015). В `ru.ts` — общие слова, меню, подписи состояний и действий (`satisfies Record<…>`:
новое значение перечисления не соберётся без подписи), подтверждения; объяснения состояний и советы — `lib/status.ts`
(советует только доступные действия); числа — `plural()`, даты — `lib/format.ts`. Тексты, нужные одной странице,
пишутся прямо в JSX. Сообщения сервера показываются без переформулировки. Названия кнопок цитируются в советах,
README и e2e — менять вместе.

### 7.5. Конструктор

React Flow (`@xyflow/react`). Источник истины — `data.definition` (`NodeDefinition`) каждого узла; `lib/graph.ts`
переводит сценарий в узлы и связи и обратно (`toDefinition`), у входа всегда `in`, у выхода — id порта, один выход —
одна связь. Палитра, форма свойств и карточки строятся по `GET /api/node-types`; проверка — только на сервере
(`POST /scenarios/validate` через 400 мс после правки, побеждает последний запрос). Правки — `editor.commit` с
отменой (100 шагов, склейка по ключу за 800 мс); выделение и перетаскивание — `editor.replace`. Группы — только в
`definition.editor.groups` (`lib/groups.ts`, id на холсте `group:…`). Вставка блоков (копирование, запись действий,
помощник) — общий формат `BlockClipboard`. Сохранение — с `expectedVersion` (409 — кто-то сохранил раньше); запуск
сначала сохраняет и отказывается, пока есть ошибки. Страница процесса показывает ту же схему только для чтения с
состояниями блоков.

Что интерфейс **не** подхватывает из описания блока сам: иконку (белый список `nodeIcons` в `Icon.tsx`, иначе `Box`),
цвет нового раздела (`categoryColor` + `--cat-<id>` во всех трёх блоках тем), редактор нового `FieldKind` (иначе
однострочное поле в `FieldEditor`), выбор по `Lookup` (`FieldEditor`/`Inspector`), особую сводку на карточке
(`nodeSummary`), цвет связи по id выхода (`edgeClasses`), списки блоков на странице «Интеграции» (текст).

### 7.6. Сборка и разработка

- `npm run build` = `tsc -b && vite build` → `src/Pepa.Host/wwwroot` (папка очищается, в git не хранится; статические
  файлы — в `ui/public`). `Pepa.Host.csproj` копирует `wwwroot/**` в вывод при сборке .NET — после сборки интерфейса
  пересоберите сервер или запустите его с `--ui src/Pepa.Host/wwwroot`.
- `MapInterface`: `index.html` — `no-cache`, файлы с хешем — `immutable` на год, неизвестные пути — `index.html`;
  нет сборки — страница «Интерфейс не собран».
- Горячая перезагрузка: `pepa-server --port 5180 --token <строка>` (команда целиком — в README), затем `npm run dev` —
  Vite на 5173 проксирует `/api` на `127.0.0.1:5180` с `changeOrigin` (иначе проверка `Host` даёт 400); открыть
  `http://localhost:5173/#token=<та же строка>`.
- Проверки: `npm test` (vitest, среда `node`, только `src/**/*.test.ts`), `npm run typecheck`, `npm run e2e`
  (`ui/e2e/smoke.cjs` против настоящего `pepa-server`, подробно — TESTING.md).

## 8. Неочевидное (частые ловушки)

- Время — только через `TimeProvider` (`context.Time`, `CancellationTokenSource(timeout, time)`): в проверках движка
  время виртуальное (`EngineHarness` + `FakeTimeProvider`), и ничего зависящего от времени не случится, пока проверка
  его не сдвинет. Маршрутизатор событий — исключение (запасной опрос по реальным 5 с).
- Замок записи не повторно входимый: из `decide`/`extraWrites` и обработчиков внутри транзакции нельзя вызывать то, что
  само открывает запись (`PublishEventAsync`, `AddJournalNoteAsync`, `CancelAsync`…).
- Движок выполняет процессы в фоне: `StartProcessAsync`/`ResumeAsync` возвращаются раньше, чем что-то выполнено.
- `NodeResult.Fail` по умолчанию повторяемый; `Effect = None` с настоящим побочным эффектом ломает восстановление.
- Изменение `IPepaStore`/`IStoreWrite` требует правки обёрток в проверках (`FaultInjectingStore`, `HookStore` в
  `ReviewRegressionTests.cs`); изменение интерфейсов адаптеров — подделок, подключённых и в `Pepa.Host.Tests`.
- `PUT /api/settings` — явный список полей (6.6); `UpdateProcessAsync` — только изменяемые столбцы (4.2).
- Новые блоки должны быть в реестре до создания движка и помощника; новые источники событий запускаются после
  `engine.StartAsync` (иначе `engineRef` ещё пуст).
- Интерфейс: новая страница — вариант в `Route`, ветка в `parseRoute`, в `href` (если есть параметры) и в `switch`
  страниц `App.tsx` (у него нет `default` — пропуск не заметит компилятор); собранный интерфейс сервер берёт из своей
  папки `bin/…/wwwroot`.
- Записи действий жёстко знают типы и поля блоков (`ui/src/lib/recording.ts`, `DesktopRecording.cs`), подсказки
  помощника — тоже (`ScenarioPrompt`): переименование поля ломает их без ошибки сборки.

## 9. Как добавить…

Подробные рецепты — в `AGENTS.md`. Кратко, что затрагивает каждое изменение:

- **Блок** — исполнитель и описание в адаптере (2.2–2.4), регистрация в `Register<X>Nodes` (новый адаптер — ещё и в
  цепочке `PepaHost.StartCoreAsync` и в `PluginLoader.ReservedPrefixes`), иконка и цвет в интерфейсе (7.5), проверки
  в `tests/Pepa.Adapters.<X>.Tests` (успех, отказ, неизвестный итог → проверка, падение посреди шага).
- **Вид поля** — `FieldKind`, `ScenarioValidator`, `InputResolver`, `ScenarioPrompt`/`DraftConverter`,
  `ui/src/api/types.ts`, `FieldEditor.tsx`.
- **Намерение или вид ожидания** — `NodeResult`, оба `switch` в `CommitResultAsync`, `WaitKind`,
  `GetRunnableProcessIdsAsync`, `TryResolveWaitAsync`, `ReleaseTokenWaitAsync`, `OnTerminalAsync`, восстановление.
- **Столбец базы** — новая миграция в конце `Migrations`, запись, `SqliteWrite` (вставка и, если изменяемый,
  `UpdateProcessAsync`), `SqliteMapping`; все реализации `IPepaStore` в проверках.
- **Адрес API** — `ApiEndpoints`, `Dtos`, маскировка, при изменении состояния — `events.Publish`; в интерфейсе —
  `client.ts`, `types.ts`, `hooks.ts`, `events.ts`; таблица 6.5 этого документа.
- **Настройка** — `PlatformSettings`, проверка в `SettingsService`, `ToEngineOptions` (если для движка), `SettingsDto`,
  явное поле в `PUT /api/settings`, `ToDto`, `SettingsPage.tsx`. Секрет — не в `PlatformSettings` (6.6).
- **Источник событий** — стабильный `ExternalId`, `OccurredAt`, `SourceCatchingUp`/`SourceCaughtUp`, запуск после
  `engine.StartAsync`, строка в таблице 3.8.
- **Решение пользователя** — `ResumeAction`/`ProcessAction`, `GetAllowedActions`, метод в `ProcessEngine.Recovery.cs`,
  подписи в `ru.ts`, `lib/status.ts`, `components/process.tsx`.

После изменения поведения — запись в DECISIONS.md (следующий номер D-0NN), строка в TESTING.md, при необходимости —
CHECKLIST.md и README.
