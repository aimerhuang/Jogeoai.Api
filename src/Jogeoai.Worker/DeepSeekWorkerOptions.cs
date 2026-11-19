namespace Jogeoai.Worker;

/// <summary>
/// DeepSeek 任务 Worker 的运行参数；绑定配置节 <c>DeepSeek:Worker</c>（默认 BatchSize 2、MaxConcurrency 2、LeaseSeconds 300）。
/// <c>DeepSeek:Worker:Enabled=false</c> 时不注册 Worker，用于回滚。
/// </summary>
public sealed class DeepSeekWorkerOptions : ApiAnswerWorkerOptions;
