using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DshNotifyicon.Services
{
    public enum EnvStatus { Ok, Missing, Outdated, Error }

    public class EnvItem
    {
        public string Name;
        public EnvStatus Status;
        public string Detail;
    }

    /// <summary>
    /// 一键体检：Node.js / npm 镜像源 / dsh 安装与版本，聚合为可展示的条目列表。
    /// 整体限时 120s（取消信号贯穿 npm 子进程），网络不可达时快速降级，绝不卡死界面。
    /// 展示文案走 Loc（中英双语，随界面语言切换）。
    /// </summary>
    public static class EnvironmentCheckService
    {
        public static async Task<List<EnvItem>> CheckAllAsync(Settings settings, string envPath, Action<string> log = null)
        {
            var items = new List<EnvItem>();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
            {
                var ct = cts.Token;

                // PATH 体检（纯本地、必须最先跑）：.NET Framework 的 Path.Combine 会拒绝非法路径
                // 字符，PATH 里一段脏数据就足以让 Node/pnpm 检测与 dsh 启动全部失败。先点名问题条目，
                // 后面的检测才有意义（脏段已在 NodeService.RefreshPath 里被丢弃，不会再抛异常）。
                log?.Invoke(Loc.T("ec.checkingPath"));
                var badSegments = PathGuard.UnsafeSegments();
                items.Add(new EnvItem
                {
                    Name = "PATH",
                    Status = badSegments.Count == 0 ? EnvStatus.Ok : EnvStatus.Error,
                    Detail = badSegments.Count == 0
                        ? Loc.T("ec.pathOk")
                        : Loc.T("ec.pathBad", badSegments.Count, string.Join("\n", badSegments))
                });

                // Node.js
                log?.Invoke(Loc.T("ec.checkingNode"));
                try
                {
                    var node = await NodeService.DetectAsync(settings.NodePath, envPath);
                    // 手动指定的路径无效时不能闷着回退：那样界面看起来一切正常，
                    // 用户却以为"我指定的那个 node 正在被使用"。
                    var hint = node.OverrideInvalid ? "\n" + Loc.T("ec.overrideFallback", settings.NodePath) : "";
                    if (node.NodeExe == null)
                    {
                        items.Add(new EnvItem { Name = "Node.js", Status = EnvStatus.Missing, Detail = Loc.T("ec.nodeMissing") + hint });
                    }
                    else
                    {
                        items.Add(new EnvItem
                        {
                            Name = "Node.js",
                            Status = EnvStatus.Ok,
                            Detail = "node " + (node.NodeVersion ?? "?") + " / npm " + (node.NpmVersion ?? "?") + "  @ " + node.NodeExe + hint
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    items.Add(new EnvItem { Name = "Node.js", Status = EnvStatus.Error, Detail = Loc.T("ec.timeout") });
                }
                catch (Exception ex)
                {
                    items.Add(new EnvItem { Name = "Node.js", Status = EnvStatus.Error, Detail = Loc.T("ec.checkFailed", ex.Message) });
                }

                // npm 镜像源
                log?.Invoke(Loc.T("ec.checkingRegistry"));
                string reg = "";
                bool regError = false;
                try { reg = (await NpmService.GetRegistryAsync(settings.MirrorUrl, envPath, ct)).Trim(); }
                catch (OperationCanceledException) { reg = Loc.T("ec.regTimeout"); regError = true; }
                catch (Exception ex) { reg = Loc.T("ec.regReadFail", ex.Message); regError = true; }
                items.Add(new EnvItem
                {
                    Name = "npm 镜像源",
                    Status = regError ? EnvStatus.Error : EnvStatus.Ok,
                    Detail = Loc.T("ec.regGlobal", reg) +
                             (string.IsNullOrEmpty(settings.MirrorUrl) ? "" : "\n" + Loc.T("ec.regToolAppend", settings.MirrorUrl))
                });

                // pnpm（dsh 插件管理必需，纯本地检查）
                log?.Invoke(Loc.T("ec.checkingPnpm"));
                var pnpm = NpmService.FindPnpm(envPath, settings.PnpmPath);
                var pnpmHint = pnpm.OverrideInvalid ? "\n" + Loc.T("ec.overrideFallback", settings.PnpmPath) : "";
                if (pnpm.Path == null)
                {
                    items.Add(new EnvItem { Name = "pnpm", Status = EnvStatus.Missing, Detail = Loc.T("ec.pnpmMissing") + pnpmHint });
                }
                else
                {
                    items.Add(new EnvItem { Name = "pnpm", Status = EnvStatus.Ok, Detail = Loc.T("ec.pnpmOk", pnpm.Path) + pnpmHint });
                }

                // dsh 本地版本（无网络）
                log?.Invoke(Loc.T("ec.checkingDsh"));
                string local = "";
                try { local = await NpmService.GetDshLocalVersionAsync(envPath, ct); }
                catch { }
                if (string.IsNullOrEmpty(local))
                {
                    items.Add(new EnvItem { Name = "dsh", Status = EnvStatus.Missing, Detail = Loc.T("ec.dshMissing") });
                }
                else
                {
                    // dsh 远端版本（网络操作，45s 内必返回）
                    log?.Invoke(Loc.T("ec.checkingDshLatest"));
                    string latest = null;
                    try { latest = (await NpmService.GetDshLatestVersionAsync(settings.MirrorUrl, envPath, ct)).Trim(); }
                    catch (Exception ex)
                    {
                        latest = null;
                        log?.Invoke(Loc.T("ec.dshLatestFail", ex.Message));
                    }
                    if (string.IsNullOrEmpty(latest))
                    {
                        items.Add(new EnvItem { Name = "dsh", Status = EnvStatus.Ok, Detail = Loc.T("ec.dshOkNoLatest", local) });
                    }
                    else if (Semver.Compare(local, latest) < 0)
                    {
                        items.Add(new EnvItem { Name = "dsh", Status = EnvStatus.Outdated, Detail = Loc.T("ec.dshOutdated", local, latest) });
                    }
                    else
                    {
                        items.Add(new EnvItem { Name = "dsh", Status = EnvStatus.Ok, Detail = Loc.T("ec.dshUpToDate", local) });
                    }
                }
            }
            return items;
        }
    }
}
