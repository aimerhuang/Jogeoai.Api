export const FIXTURE_IDS = {
  tenantId: "11111111-1111-4111-8111-111111111111",
  adminUserId: "11111111-1111-4111-8111-111111111112",
  brandProjectId: "22222222-2222-4222-8222-222222222222",
  primaryBrandId: "22222222-2222-4222-8222-222222222223",
  competitorBrandId: "22222222-2222-4222-8222-222222222224",
  primaryBrandRuleVersionId: "22222222-2222-4222-8222-222222222225",
  competitorBrandRuleVersionId: "22222222-2222-4222-8222-222222222226",
  parserVersionId: "80000000-0000-4000-8000-000000000001",
  matrixId: "66666666-6666-4666-8666-666666666666",
} as const;

export const PLATFORM_FIXTURES = [
  { code: "doubao", name: "豆包", region: "cn" },
  { code: "ernie", name: "文心一言", region: "cn" },
  { code: "zhipu_qingyan", name: "智谱清言", region: "cn" },
  { code: "qwen", name: "千问", region: "cn" },
  { code: "kimi", name: "Kimi", region: "cn" },
  { code: "deepseek", name: "DeepSeek", region: "cn" },
  { code: "tencent_yuanbao", name: "腾讯元宝", region: "cn" },
  { code: "chatgpt", name: "ChatGPT", region: "overseas" },
  { code: "claude", name: "Claude", region: "overseas" },
  { code: "gemini", name: "Gemini", region: "overseas" },
] as const;

export const SYNTHETIC_QUESTIONS = [
  "【合成测试】适合小型团队使用的项目管理工具有哪些？",
  "【合成测试】如何选择支持中文协作的任务管理软件？",
  "【合成测试】远程团队需要哪些高效协作工具？",
  "【合成测试】哪些项目管理产品适合初创企业？",
  "【合成测试】团队看板工具通常应该具备哪些能力？",
  "【合成测试】如何比较不同任务管理平台的易用性？",
  "【合成测试】适合市场团队的工作流软件有哪些？",
  "【合成测试】多人协作时怎样减少任务遗漏？",
  "【合成测试】项目进度透明化可以使用哪些工具？",
  "【合成测试】如何为十人团队配置项目管理流程？",
  "【合成测试】品牌监测平台应该关注哪些核心指标？",
  "【合成测试】如何衡量品牌在人工智能回答中的可见度？",
  "【合成测试】企业如何持续跟踪竞品的线上声量？",
  "【合成测试】哪些工具可以记录人工智能回答的引用来源？",
  "【合成测试】如何建立可追溯的品牌提及监测体系？",
  "【合成测试】跨平台品牌监控需要保存哪些原始证据？",
  "【合成测试】如何避免把采集失败误判为品牌表现下降？",
  "【合成测试】品牌提及率和推荐排名应该怎样计算？",
  "【合成测试】如何比较多个问答平台的品牌回答差异？",
  "【合成测试】人工智能可见度报告需要展示哪些样本信息？",
  "【合成测试】企业部署浏览器自动化时应注意哪些安全问题？",
  "【合成测试】如何设计可恢复的定时采集任务？",
  "【合成测试】分布式采集节点怎样避免重复写入结果？",
  "【合成测试】任务租约过期后应该如何安全地重新领取？",
  "【合成测试】国内外采集节点如何实现严格区域路由？",
  "【合成测试】浏览器会话失效时系统应该如何处理？",
  "【合成测试】自动化采集中的迟到结果应该怎样审计？",
  "【合成测试】如何为采集证据设计合理的留存周期？",
  "【合成测试】多租户监控系统如何防止数据越权？",
  "【合成测试】上线前应如何验证采集系统的可靠性？",
] as const;

export function fixtureQuestionId(index: number): string {
  return `40000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`;
}

export function fixtureQuestionVersionId(index: number): string {
  return `50000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`;
}

export function fixtureTopicGroupId(index: number): string {
  return `30000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`;
}
