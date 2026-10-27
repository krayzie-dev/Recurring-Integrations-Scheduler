# Quartz.NET 4.6.2 – notes from the RIS 5.0 spike

Verified on 2026-10-27 with a runnable spike and covered by `test/RIS.Jobs.Tests/QuartzIntegrationTests.cs`.

## API used by RIS

| Need | Quartz 4 API | Notes |
|---|---|---|
| Job with typed options | `IJob<TInput>.Execute(context, input, ct)` returns `ValueTask` | RIS uses `RisQuartzJob : IJob<RisJobInput>` |
| Attach options to a job | `JobBuilder.Create<T>().UsingInput(input)` | Input is serialized with System.Text.Json and **recorded in execution history** (`RecordInput`). Never put secrets in it – RIS references connections and SFTP servers by name. |
| Polymorphic options | `RisJobInput { JobOptionsBase Options }` envelope | Serializing the abstract type through a property keeps the `"type"` discriminator; verified by test. |
| Run result | `context.Result = JobRunReport.Succeeded/Failed/Skipped(summary).With(key, value)` | A `Failed` report **without throwing** is stored as Failed with summary and metrics, and Quartz does not log the error again. RIS logs each error once in `JobPipeline`. |
| Job exceptions | thrown exception | Quartz logs it twice (`JobRunShell` 1053 and `ErrorLogger` 1081). RIS only lets `OperationCanceledException` escape. |
| Execution history | `services.AddQuartzExecutionHistory(o => …)` (in memory); `UsePersistentStore(s => s.UseExecutionHistory())` (database, stage 3) | `ExecutionHistoryEntry`: `Summary`, `MetricsJson`, `Log`, `Input`, `FireInstanceId`, `EffectiveResult`. |
| Job log in history | `q.UseExecutionLogCapture(o => o.MaxLines = …)` | Implemented as a logger provider. **It must stay registered**: `ClearProviders()` after `AddQuartz` removes it. With Serilog use `AddSerilog(..., writeToProviders: true)`. |
| Progress in dashboard | `context.ReportProgress(percent, message)` | Used for polling and per-file progress. |
| Schedule paused job | `scheduler.ScheduleJob(job, triggers, new ScheduleJobOptions { Replace = true, Paused = true, PauseReason = … })` | `replace:` named argument from 3.x no longer exists. |
| Misfire | `WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed)` / `SimpleTriggerMisfireInstruction.NextWithRemainingCount` | 3.x `WithMisfireHandlingInstruction…` methods were removed. |
| Interrupt | `IScheduler.Interrupt(JobKey, ct)`, `context.CancellationToken` | Shutdown cancels running jobs; RIS logs interrupted export execution ids. |
| Named schedulers | `AddQuartz("name", …)` registers keyed services | RIS uses the default (unnamed) scheduler. |

## Trimming / AOT

Quartz job input serialization needs System.Text.Json metadata. With trimming or Native AOT (also the default of
`dotnet run file.cs`) it fails with *"JsonTypeInfo metadata for type … was not provided"*. RIS is published without
trimming; if that changes, register a `JsonSerializerContext` for `RisJobInput` through Quartz (`AddJsonTypeInfoResolver`).

## Upgrade policy

Quartz 4 minor versions add mandatory database migrations (4.3, 4.6, announced 4.7). The version is pinned in
`src/Directory.Packages.props`; upgrades are done deliberately with tests and with the schema migration step (stage 3: Quartz.Weasel.SQLite).
