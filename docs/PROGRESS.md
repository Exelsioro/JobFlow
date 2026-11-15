# JobFlow — фактический прогресс

Дата: 2026-11-15. **Статус: работающий первый срез; полный MVP НЕ завершён.**

## Репозиторий и окружение

- Проект: `D:/Projects/JobFlow`.
- Git инициализирован до реализации. Начальный коммит: `9eb349d`.
- Domain & Persistence: `c549ac7`.
- API / Worker / CLI / браузерные тестовые адаптеры: `1502671`.
- Последующие коммиты содержат Angular, smoke-проверки и эту документацию; см. `git log --oneline`.
- MIT выбрана для свободного self-hosting и модификаций. GitHub-репозиторий не создавался.
- .NET SDK на машине: 10.0.203 и 10.0.401; использован .NET 10.
- EF Core 10.0.15, Playwright .NET 1.64.1, Angular 22.2.x (точные npm версии в lockfile), Node 26.0.0.
- Персональные сведения, токены, реальные вакансии и внешние журналы в код не включены.

## Проверено выполнением

| Проверка | Результат |
|---|---|
| Backend build | Успешно, 0 предупреждений и ошибок |
| Angular production build | Успешно, около 317 КБ исходного bundle / 80 КБ сжатого |
| Unit tests | 7 прошли |
| Integration tests | 10 прошли |
| Реальный Codex CLI | Синтетический запрос вернул `{"summary":"connection-ok"}`; JSON Schema проверена локально |
| Playwright happy path | Форма заполнена, получена TEST-RECEIPT-001 |
| Неизвестное обязательное поле | Blocked, успех не заявлен |
| Потеря авторизации тестового сайта | Blocked, требуется ручная авторизация |
| Отсутствие квитанции | SubmissionUnknown, повтор не выполняется |
| Извлечение тестовой вакансии | Playwright прочитал DOM встроенной искусственной страницы |
| HTTP API | Пустой запуск, профиль, загрузка версии PDF, площадка, вакансия, агент, provider, задание |
| Дубли | Повторная вакансия/отклик отклонены уникальными индексами |
| Владение CV | Версия чужого профиля отклонена |
| Состояния | Незаконные переходы и устаревшая ревизия отклонены |
| Параллельные исполнители | Одно задание захватывает ровно один процессор |
| Сбой Worker | Истёкшая lease восстанавливается; старый Running становится Interrupted |
| Ручной запуск | Не включает постоянное расписание |
| Сбой провайдера | Failed и версия конфигурации сохранены; вход/выход не сохраняются |
| БД только для чтения | Ошибка не маскируется успешным запуском |
| Перезапуск хранения | Данные сохранились после закрытия и повторного открытия БД |
| Angular в Chromium | Создание профиля через UI и сохранение после reload |
| UI navigation | Back/Forward проверены с ожиданием фактической смены URL |
| UI profile scope | Задания другого профиля скрываются; устаревшие выборы сбрасываются |
| UI queue status | Отображается постановка в очередь, а не завершение |
| Отдельный Worker | Запущен как отдельный процесс и обработал задание |
| Desktop / mobile | Снимки 1440 и 390px; горизонтального переполнения страницы нет |

Последние тесты: `dotnet test` — 17/17. Smoke: `dotnet run --project tests/JobFlow.Smoke` — PASS. Реальный AI: `dotnet run --project tests/JobFlow.Smoke -- --codex` — PASS.

UI-review подтвердил исправление четырёх конкретных замечаний (фильтрация, history navigation, queued status, дубли вариантов резюме). Это не аудит всех экранов и не подтверждение полноты MVP. Linux, реальная ручная авторизация браузерных профилей и миграция на другой компьютер не проверены.

## Этапы

| Этап | Состояние |
|---|---|
| 1 Bootstrap | Выполнен: Git, solution, проекты, лицензия, структура |
| 2 Domain & Persistence | Базовый слой выполнен: сущности, FK, индексы, migrations, тесты; модель ещё должна развиваться |
| 3 API & Worker | Рабочий базовый слой; не все use cases вынесены из HTTP handlers |
| 4 Angular UI | Рабочие основные формы; часть требуемых разделов явно обозначена незавершённой |
| 5 AI Provider | Реальный Codex CLI работает; полноценные capability/cost/tool/retry настройки отсутствуют |
| 6 Browser Automation | Dry-run и тестовый discovery работают; профили реализованы, ручной lifecycle не проверен |
| 7 Workflow | FSM и ручные переходы работают; целостная автоматическая цепочка не реализована |
| 8 Integrations | CSV export работает; import/sync/email/MCP отсутствуют |
| 9 Scheduler | Интервалы, manual run, pause/resume, history и recovery работают; расширенные функции отсутствуют |
| 10 Hardening | Начат; тесты/документация готовы для реализованного среза, полный acceptance matrix не закрыт |

## Обязательная оставшаяся работа

1. **Application use cases и workflow.** Вынести оркестрацию из Program.cs; соединить discovery → evaluation → letter → preparation → review. Привязывать агента к этапу через явную конфигурацию. Сохранить историю оценки и представить её в UI.
2. **Полная оценка вакансии.** Сейчас только исключённые работодатели и совпадения слов. Зарплата, свежесть, локация, уровень, обязательные требования и профильные правила автоматически не интерпретируются. AI-оценка не встроена в кнопку «Оценить».
3. **Агенты.** Нет управления разрешёнными инструментами, browser provider, retry/policy и библиотекой полноценных шаблонов. Типы задач есть, но не являются автоматическими workflow-шагами. Нельзя считать наличие интерфейса IAgentExecutor завершённой оркестрацией.
4. **MCP Client.** Пока есть только модель ToolConnection и честное сообщение в UI. Нужны официальный client SDK, stdio/HTTP transports, discovery, permission binding, secure environment/secrets и тестовый сервер. MCP Server не нужен.
5. **Резюме и аккаунты.** Нужны primary/specialized CV UX, platform bindings, external resume IDs и проверка актуальности. PDF сейчас проверяется по размеру и сигнатуре; извлечение текста/антивирусная проверка не реализованы.
6. **Подготовка и отправка.** Сопроводительное письмо можно задать при создании отклика, но нет полноценного редактора черновика и связанной AI-генерации письма. ApplicationAttempt и идемпотентный ключ пока только модель. Нет исполняемого IJobApplicationProvider, подтверждений реальной площадки, reconciliation SubmissionUnknown и управляемого включения отправки. Реальную отправку сохранять отключённой.
7. **Интеграции и импорт.** Нет CSV-import, Google Sheets, field mapping, direction/conflict policy, initial import confirmation workflow и sync history. Guard для unconfirmed import существует, но подключение интеграции ещё не реализовано.
8. **Мониторинг.** Нет Gmail/почтового источника, автоматической проверки статусов, приглашений/отказов и интерфейса взаимодействий. EmployerInteraction — модель, не работающий мониторинг.
9. **Scheduler.** Только HealthCheck и BrowserDryRun. Нужны AI/discovery/monitoring/sync dispatch, cron при необходимости, per-job retry/backoff/timeout, cancellation активного задания, lease renewal и heartbeat Worker. Пауза сейчас прекращает будущие запуски, но не текущий. Часовой пояс сохраняется и проверяется; интервал UTC, даты UI отображаются в поясе браузера.
10. **Browser profiles.** Проверить полный ручной lifecycle, account/platform bindings и запрет удаления между несколькими API-процессами. Есть файловая блокировка открытого профиля, но MVP предполагает один API-процесс. BrowserAct не поддерживается.
11. **Provider controls.** Реализовать capabilities, проверку доступных моделей, дополнительные провайдеры, cost controls где возможны. Endpoint не используется Codex CLI. Не выдумывать идентификаторы моделей.
12. **Конфиденциальность и безопасность.** Нет отдельного secret vault, OAuth credential store, configurable retention/redaction, auth для remote access. Конфигурационные снимки могут содержать введённый пользователем текст. Не размещать текущую версию в публичной сети.
13. **Hardening.** Нужны негативные API schema/size tests, cancellation/process-tree tests, полноценная перезагрузка хостов в тестах, интеграционные сбои/повторы, browser profile tests, PostgreSQL/Linux smoke, backup/restore drill, service packaging и полный аудит доступности.

## Точные команды продолжения

Из корня проекта, PowerShell 7:

```powershell
Set-Location D:/Projects/JobFlow
git status --short
git log --oneline -5
dotnet tool restore
pwsh -File scripts/build.ps1
pwsh -File tests/JobFlow.IntegrationTests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test
dotnet run --project tests/JobFlow.Smoke
```

AI-проверка отдельно и только при настроенном Codex:

```powershell
dotnet run --project tests/JobFlow.Smoke -- --codex
```

Запуск приложения — два отдельных терминала:

```powershell
dotnet run --no-build --no-launch-profile --project src/JobFlow.Api
dotnet run --no-build --no-launch-profile --project src/JobFlow.Worker
```

Адрес: http://127.0.0.1:5271. При необходимости задать одинаковый JOBFLOW_DATA_DIR в обоих терминалах. Smoke сам использует отдельный `data/smoke-*`; данные пользователя не читает.

После изменения сущностей:

```powershell
dotnet ef migrations add DescriptiveChangeName --project src/JobFlow.Infrastructure
dotnet test
```

Следующий приоритет: Application orchestration + подготовка/редактирование письма, затем MCP Client и journal import gate. Не включать отправку реальных откликов, пока не реализованы и не проверены idempotency, reconciliation и явное разрешение пользователя.

## Продолжение: workflow
Реализованы сохраняемые определения и история workflow, этапы поиска/оценки/письма/подготовки, профильные структурированные правила, назначение AI-агентов, редактор письма и продолжение после сбоя. Реальная отправка отключена. Проверено: 20 тестов (7 unit + 13 integration), backend и Angular production build. MCP и интеграции — следующий этап.

