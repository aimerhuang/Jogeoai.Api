-- 031_pro_plan_brand_member_limits.sql
-- 专业版权益调整为 20 个品牌、20 位成员（成员包含所有者和邀请中成员）。
-- 手动执行于 001–030 之后；API / Worker 启动不执行本文件。
-- 部署前记录目标库 pro 原额度；如存在自定义更高或不限额度，须明确确认调整后执行。
-- 只更新两项目录权益；已有订阅立即读取新上限，不改周期、余额、积分或其他套餐。
-- 赋值可重放，已为 20/20 时无变更。

UPDATE subscription_plans
SET max_brands = 20, max_members = 20
WHERE code = 'pro';
