# No PMC Truce

修复 SPT 5.0（EFT 1.3.0.1.48558）里 **USEC 与 BEAR 的 PMC 不互相攻击**的问题。

- GUID：`com.dsh.nopmctruce`
- 版本：2.1.0
- 依赖：BepInEx 5（IL2CPP）、sptushonka（SPT 客户端本体）

## 根因

SPT 服务端在下发 bot 前把每个 PMC 的阵营压成 Savage（`BotController.cs:290-294`，注释写明客户端要求如此）。4.1.x 由客户端补丁在 `BotCreationData.ChooseProfile` 里还原，**5.0 发行版没有这个补丁**。

于是客户端里所有 PMC 的 `Profile.Info.Side` 都是 `Savage`，USEC / BEAR 只剩 `Settings.Role`（51 / 52）的区别。而敌意判定按**目标的 Side** 选行为字段：Bear→`BearPlayerBehaviour`、Usec→`UsecPlayerBehaviour`、**Savage→`SavagePlayerBehaviour`（值 `Warn`，不攻击）**。所有 PMC 都落到最后一档，服务端那份 100% 敌对的表被完全旁路。

## 修复

三层，都只改客户端内存，不碰配置文件与游戏文件。

**1. 还原阵营（前提）** — 在 `BotCreationData.ChooseProfile`（4.1.x 官方位置）与 `BotOwner.Create`（兜底）两个 postfix 里，把 pmcBEAR 的 `Info.Side` 写回 `Bear`、pmcUSEC 写回 `Usec`。写的是 `ProfileInfo._Side_k__BackingField`，因为 5.0 里 `Info.Side` 的 setter 是 protected。

**2. 跨营敌意** — 在 `BotGroupHostility` 构造前修正服务端下发的敌意表，只动 pmcBEAR / pmcUSEC 条目里指向对方的项。

**3. 同营敌意** — 原版同阵营永不互打，因为数据里根本没有「指向自己阵营」的概率项，查不到就不掷骰。这里补上该条目，让不同小队的同阵营 PMC 也能开战。小队内部不受影响。

## 配置（F12）

| 节 | 项 | 默认 | 说明 |
|---|---|---|---|
| 1. 核心修复 | `EnableSideRestore` | `true` | 阵营还原总开关，关掉则本插件无效 |
| 1. 核心修复 | `EnableHostilityTuning` | `true` | 第 2、3 层的总开关 |
| 2. 跨营敌意 | `Behaviour` | `AlwaysEnemies` | `RespectBackend` 按服务端原值 / `Chanced` 按概率 / `AlwaysEnemies` 无条件敌对 |
| 2. 跨营敌意 | `Chance` | `-1` | `Chanced` 模式下的跨营概率。`-1` 不干预，沿用原值（海关/森林 25、灯塔 35、立交桥 15、迷宫 75） |
| 3. 同营敌意 | `EnableSameFaction` | `true` | 同阵营不同小队之间也可开战 |
| 3. 同营敌意 | `SameFactionChance` | `100` | 同营概率，与跨营独立。`-1` 恢复原版 |
| 4. 关系修正 | `RemovePmcFromNeutral` | `true` | 把对手从 Neutral 移除（中立优先级高于概率） |
| 4. 关系修正 | `RemovePmcFromAlwaysFriends` | `true` | 把对手从 AlwaysFriends 移除（友好优先级最高） |
| 4. 关系修正 | `KeepZryachiyFriendly` | `true` | 保留与 Zryachiy 系（29/30/38/40）的友好，否则会和灯塔守卫打起来 |
| 5. 诊断 | `Verbose` | `false` | 每个补丁点首次命中、每次改动都打日志 |

想要原版手感：`Behaviour = RespectBackend`、`EnableSameFaction = false`。

## 安装

把 `NoPmcTruce.dll` 与 `NoPmcTruce.deps.json` 放进 `BepInEx/plugins/NoPmcTruce/`。

卸载：删该目录，**并删** `BepInEx/config/com.dsh.nopmctruce.cfg`，否则重装会沿用旧设置。

## 验证

开 `Verbose = true` 后看 `BepInEx/LogOutput.log`：

```
No PMC Truce 2.1.0 已加载（Side 还原=True，敌意调整=True，跨营=AlwaysEnemies/-1，同营=True/100）。
[restore/ChooseProfile] pmcUSEC: Savage -> Usec (nick=..., total=1)
[hostility] 敌意表已修正（第 1 次应用，PMC 条目 2 条）：跨营模式=AlwaysEnemies，... 同营概率改写 2。
```

出现 `Savage -> Usec` / `Savage -> Bear` 即第 1 层生效；`同营概率改写 2` 是原版缺失项被补上的证据（正常恒为 2）。

## 兼容性

只 patch `BotCreationData.ChooseProfile`、`BotOwner.Create`、`BotGroupHostility` 构造器三处。已扫描 `BepInEx/plugins` 下全部 DLL，无其它插件占用这些目标。不改刷怪、不写配置，可与 KORD BREACH、For Humanity、BetterEFT、DynamicMaps 等共存。所有补丁体内部 try/catch，失败只记日志，不影响游戏启动。

## 构建

需要本机有 SPT 安装（提供 BepInEx 与 interop 程序集引用）。

```bash
./build.sh "D:\EFT-5.0 BE"
```

产物在 `src/bin/Release/`。

## 许可

MIT
