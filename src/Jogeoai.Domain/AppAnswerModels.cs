namespace Jogeoai.Domain;

/// <summary>App 回答里没有可用 URL 的一条引用来源。它不进入引用表，也不参与任何引用统计，只用于回答详情展示。</summary>
/// <param name="Title">来源标题。</param>
/// <param name="Site">站点名；界面没有展示时为空。</param>
/// <param name="List">来源所在的列表（例如 cited）；界面只有一个列表时为空。</param>
public sealed record AppSourceWithoutUrlView(string? Title, string? Site, string? List);

/// <summary>
/// App 回答的采集环境与引用读取情况，取自回答元数据的 app 块。不含账号代号、采集节点和绑定标识。
/// </summary>
/// <param name="DeviceId">采集设备的标识（模拟器名称），取自任务租约对应的绑定。</param>
/// <param name="AndroidVersion">设备的 Android 版本。</param>
/// <param name="AppPackage">App 包名。</param>
/// <param name="AppVersion">采集时的 App 版本。</param>
/// <param name="WebSearch">界面上可见的联网搜索状态；采集器只读取，不操作。</param>
/// <param name="DeepThinking">界面上可见的深度思考状态；采集器只读取，不操作。</param>
/// <param name="CitationsStatus">引用来源的读取状态。</param>
/// <param name="CitationsIssue">引用读取失败的原因词；只在读取状态为 unreadable 时出现。</param>
/// <param name="CitationsTotal">界面上展示的引用来源总数（含没有 URL 的），始终完整。</param>
/// <param name="CitationsWithUrl">其中带可用 URL、已进入引用表的条数，始终完整。</param>
/// <param name="SourcesWithoutUrlTruncated">
/// 没有 URL 的引用来源是否被截断：回答元数据有 16 KB 的上限，放不下时只保留前面若干条，此时为 true，
/// 详情里的 sourcesWithoutUrl 少于 citationsTotal − citationsWithUrl。
/// </param>
/// <param name="ScreenshotSha256">完成截图文件的 SHA-256；截图本身不上传。没有截图时为空。</param>
/// <param name="PromptRuleVersion">生成该任务时使用的提问检查规则版本。</param>
public sealed record AppAnswerView(string? DeviceId, string? AndroidVersion, string? AppPackage, string? AppVersion,
    AppVisibleState WebSearch, AppVisibleState DeepThinking, AppCitationsStatus CitationsStatus, string? CitationsIssue,
    int CitationsTotal, int CitationsWithUrl, bool SourcesWithoutUrlTruncated, string? ScreenshotSha256, int PromptRuleVersion);
