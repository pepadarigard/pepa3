# Тестирование

Надёжность проверяется автоматическими тестами поведения, а не ручным запуском. Внешние сервисы заменяются
имитациями там, где это позволяет проверить логику без реальных действий.

## Уровни

| Уровень | Где | Что проверяет |
|---|---|---|
| Ядро | `tests/Pepa.Core.Tests` | проверка сценариев, выражения и их песочница, шаблоны, сериализация |
| Движок | `tests/Pepa.Engine.Tests` | исполнение, ожидания, ошибки, повторы, параллельность, восстановление — на **настоящем SQLite** и **управляемом времени** (`FakeTimeProvider`) |
| Файлы | `tests/Pepa.Adapters.FileSystem.Tests` | рабочая папка, каждое действие и его проверка результата, сквозные сценарии с движком |
| API | `tests/Pepa.Host.Tests` | настоящий экземпляр на случайном порту: доступ, сценарии, запуск, настройки, блокировка экземпляра, поток событий, перезапуск |
| Интерфейс (модули) | `ui/src/**/*.test.ts` (vitest) | преобразование сценария ↔ схема, раскладка, помощники блоков |
| Интерфейс (сквозная) | `ui/e2e/smoke.cjs` (Playwright) | живой сервер + Chromium: запуск с вводом, конструктор (добавить блок, связать, сохранить, отменить/вернуть, запустить), пауза/продолжение/остановка, занятие ресурса и ожидание события с отправкой события из интерфейса, автозапуск по расписанию, история, тёмная тема |

### Инструменты имитации

- `ExternalWorld` + `TestActionNode` — внешнее действие с управляемым поведением: успех, подтверждённая ошибка,
  исключение до/после эффекта, остановка внутри действия до команды теста, принудительный итог проверки.
  Считает и вызовы, и фактические эффекты — отдельно.
- `FaultInjectingStore` — обёртка хранилища, которая «роняет» запись в заданный момент (например, при записи
  «подтверждено» для конкретного шага), а затем — все последующие записи (жёсткий сбой).
- `EngineHarness` — движок над временной базой; «перезапуск» и «аварийный перезапуск» создают новый движок над
  тем же файлом.

## Обязательные категории ТЗ → тесты

| Категория | Тесты |
|---|---|
| ресурс профиля браузера/AdsPower | `BlockResourceTests.*`, `AdsPowerTests.Second_process_with_same_profile_waits_for_the_first` |
| последовательные шаги | `ExecutionTests.Sequential_steps_run_in_order_once_each` |
| условия, циклы, переменные | `Condition_routes_by_variable`, `Loop_with_counter_runs_body_expected_number_of_times`, `Action_output_is_saved_to_variable`, `Variable_type_mismatch_stops_process_with_clear_reason`, `Inputs_are_validated_against_declared_variables` |
| тайм-ауты и ожидания | `WaitAndConcurrencyTests.Wait_completes_only_after_its_time`, `Waiting_process_does_not_occupy_a_worker`, `Action_timeout_gives_unknown_result_and_checks_external_system`, `Next_time_of_day_is_today_or_tomorrow` |
| независимые параллельные процессы | `Instances_of_one_scenario_have_independent_variables`, `Independent_processes_run_in_parallel`, `Failure_of_one_process_does_not_affect_another` |
| конфликт ресурсов | `ResourceTests.Second_process_waits_until_resource_is_released`, `Queue_is_served_in_order_of_requests`, `Waiting_for_resource_does_not_block_other_processes`, `Resource_is_released_automatically_when_process_ends`, `Cancelling_holder_passes_resource_to_next_in_queue`, `Resource_wait_timeout_follows_timeout_branch`, `Paused_waiter_keeps_its_place_but_is_skipped_while_paused`, `Deadlock_is_detected_and_stops_with_explanation`, `After_restart_paused_holder_keeps_resource_and_stale_locks_are_released` |
| несколько событий | `EventTests.Event_goes_only_to_the_process_with_matching_key`, `Several_events_each_go_to_one_waiter_and_extra_ones_stay_unmatched`, `Event_without_waiting_process_is_stored_and_starts_nothing`, `Process_can_signal_another_process_with_send_event` |
| неоднозначное сопоставление | `EventTests.Ambiguous_event_is_not_given_to_anyone_until_user_decides`, `Higher_priority_wins_without_conflict`, `Match_condition_filters_events`, `PlatformApiTests.Conflicting_event_is_listed_and_resolved_by_user` |
| дедупликация | `EventTests.Repeated_event_with_same_external_id_is_processed_once`, `TriggerTests.Event_trigger_starts_process_once_with_values_from_event`, `PlatformApiTests.Reply_sample_waits_for_event_holds_resource_and_saves_answer` |
| события и перезапуск | `EventTests.Event_for_paused_process_is_applied_only_after_resume`, `After_restart_waiting_process_gets_event_only_after_manual_resume`, `Cancelled_waiter_returns_assigned_event_for_rematching`, `Earlier_event_is_taken_only_within_lookback_window`, `Timeout_follows_timeout_branch_and_late_event_is_not_taken` |
| расписания и запуск по событию | `TriggerTests.Interval_schedule_starts_one_process_per_occurrence`, `Overlapping_run_is_skipped_while_previous_is_still_working`, `Runs_missed_while_closed_are_counted_but_not_started`, `Waiting_process_takes_event_before_triggers`, `Schedule_calculator_handles_days_and_one_time_runs` |
| вызов сценария, перебор списка | `SubprocessAndLoopTests.*`: итог дочернего процесса, ошибка, остановка вместе с родителем, тайм-аут, пауза родителя, перезапуск обоих, предел вложенности, перебор, пустой список, вложенный цикл с ранним выходом, позиция цикла после перезапуска |
| остановка после ошибки | `ErrorHandlingTests.Failure_without_handler_stops_process_before_next_step`, `Retries_exhausted_stop_the_process`, `FileScenarioTests.Path_escape_stops_process_without_touching_files` |
| сохранение состояния и восстановление | `RecoveryTests.Waiting_state_survives_restart_with_remaining_time`, `Process_paused_by_user_stays_paused_after_restart`, `HostLifecycleTests.Waiting_process_is_paused_after_restart_and_resumes_manually` |
| сбой между внешним действием и сохранением результата | `RecoveryTests.Crash_between_action_and_saving_result_never_repeats_the_action`, `Crash_with_inconclusive_verification_requires_user_decision`, `Storage_failure_on_result_marks_process_for_attention_without_restart`, `FileScenarioTests.Crash_after_append_does_not_append_twice`, `Crash_before_write_reached_disk_writes_once_after_verification` |
| отсутствие автозапуска после перезапуска | `RecoveryTests.After_restart_nothing_continues_automatically`, `Crash_before_action_started_replans_step_once_on_resume` |
| отсутствие повтора подтверждённых действий | те же тесты восстановления + `ErrorHandlingTests.Unknown_result_is_never_retried_automatically`, `User_marks_unknown_step_as_done_and_it_is_not_repeated` |

Дополнительно: обработка ошибок по ветке «Ошибка», повторы только подтверждённых ошибок, решения пользователя
(проверить, повторить, считать выполненным, пропустить), пауза и остановка во время действия, лимит шагов,
неизменность запущенного процесса при правке сценария, защита рабочей папки (`..`, абсолютные пути, ссылки,
имена Windows), проверка результата каждого файлового действия, безопасность API (токен, Host), блокировка
второго экземпляра, сохранность выбранной рабочей папки между запусками.

Браузер и AdsPower (`Pepa.Adapters.Browser.Tests`) проверяются на настоящем Chromium и маленьком тестовом сайте:
открыть, ввести, нажать с проверкой, прочитать, снимок, закрыть; отсутствующий и неоднозначный элемент не
нажимается; потерянный после перезапуска браузер сообщается. AdsPower — на имитации Local API, которая, как
настоящий AdsPower, запускает браузер с портом отладки: открытие и закрытие профиля, ключ API, запасной v1,
пауза между запросами, второй процесс ждёт профиль, переподключение после перезапуска без повторного открытия,
закрытый пользователем профиль. Браузер для тестов: `PEPA_TEST_BROWSER_PATH` или `PEPA_TEST_BROWSER_CHANNEL`
(в CI — Edge на Windows и Chrome на Linux); без браузера эти тесты пропускаются.

Telegram (`Pepa.Adapters.Telegram.Tests`) — блоки на имитации Telegram и настоящем движке: ответ только из своего
чата, фильтр по тексту, повтор сообщения обрабатывается один раз, потерянное подтверждение отправки проверяется по
истории чата (второй раз не отправляется), сообщение за время простоя доходит до ждущего процесса без автозапуска.
Проверка отправки не принимает за своё такое же сообщение, отправленное раньше, и находит своё при обрезанных
пробелах; подтверждённая проверкой отправка заполняет переменную; отказ Telegram — ошибка, а не «итог неизвестен».
Ответ, пришедший вовремя, пока Pepa была закрыта, принимается после «Продолжить», а тайм-аут наступает только через
минуту после запуска; старое догруженное сообщение не становится ответом.
Сам сервис — на локальном «молчащем» сервере и закрытом порту: понятный тайм-аут входа, закрытие Pepa во время
входа не ждёт Telegram, неудачный вход не оставляет сессии. Приём сообщений — через настоящий менеджер обновлений
библиотеки без сети (обновления подаются в его обработчик): сообщения принимаются по порядку и не блокируют
библиотеку, состояние сохраняется; непринятое сообщение не продвигает сохранённое состояние; закрытие во время
повторов не ждёт; повреждённый файл состояния не мешает работе. Тесты хоста никогда не обращаются к настоящему Telegram.

Окна Windows (`Pepa.Adapters.Desktop.Tests`) — блоки на имитации Windows и настоящем движке: нажатие без мыши и
мышью, отсутствующее/неоднозначное/недоступное окно или элемент не нажимается, два окна с одинаковым заголовком не
угадываются, ввод проверяется чтением поля, нажатие с неизвестным итогом не повторяется, клавиатура и мышь — только
в найденном окне, сочетания клавиш проверяются при проверке сценария, два процесса не смешивают ввод (ресурс),
закрытие окна идемпотентно. Реализация для Windows (`UiaDesktop`) собирается в CI, но с настоящими программами
проверяется у пользователя.

`ReviewRegressionTests` — гонки, найденные ревью этапа 2 и воспроизведённые детерминированно (удаление расписания
во время запуска, событие до перезапуска, назначение события во время возврата, сбой во время ожидания и т.д.).

Проверка самих тестов: намеренная поломка восстановления (не помечать прерванные шаги «неизвестно») и обработки
неизвестного итога (считать его обычной ошибкой) роняет соответствующие тесты. Так же проверены тесты этапа 2:
отдача события при равных кандидатах, выдача ресурса приостановленному процессу и продолжение цикла при новом
входе — каждая поломка роняет свой тест.

## Как запустить

```bash
dotnet test Pepa.slnx            # все тесты .NET (~1 минута)
cd ui && npm test                # модульные тесты интерфейса
cd ui && npm run build && dotnet build ../src/Pepa.Server && npm run e2e   # сквозная проверка (нужен Playwright)
```

В CI (`.github/workflows/build.yml`) на каждый push: на Windows — все тесты и сборка приложения (артефакт
`Pepa-win-x64`), на Linux — все тесты и сквозная проверка интерфейса со скриншотами (артефакт `e2e-screenshots`).

## Что проверяется только вручную (на Windows)

- Само окно WPF: запуск, WebView2, выбор папки, «открыть в проводнике», предупреждение при закрытии,
  активация уже открытого окна при повторном запуске. В контейнере разработки окно только компилируется.
