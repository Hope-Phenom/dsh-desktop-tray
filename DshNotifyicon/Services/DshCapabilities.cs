using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace DshNotifyicon.Services
{
    /// <summary>
    /// 已安装 dsh 的 Web CLI 能力判定。dsh 的选项可用性只取决于已安装版本，
    /// 这里把这个判断收敛成一个小单元，避免版本规则散落在启动链路里。
    /// </summary>
    public static class DshCapabilities
    {
        /// <summary>
        /// 首个接受 <c>--no-open</c> 的 dsh 版本。实测全部已发布版本：0.0.1-rc.1/rc.2/rc.5 与
        /// 0.1.0-rc.2/rc.3/rc.6/rc.7 都不带该选项，0.1.0-rc.8 起才有。
        /// 老版本的内层 commander 未开 allowUnknownOption，误传会以
        /// "error: unknown option '--no-open'" 退出 1，所以阈值必须精确。
        /// </summary>
        public const string NoOpenSinceVersion = "0.1.0-rc.8";

        /// <summary>读取 package.json 时要求匹配的包名，避免布局变化后读到无关包。</summary>
        const string DshPackageName = "@deepseek-ai/dsh";

        /// <summary>
        /// 是否支持 --no-open（dsh 不再自己弹浏览器，弹出与否完全由本工具决定）。
        /// 判定刻意保守：版本串必须能被严格解析且不低于阈值，任何不确定都返回 false ——
        /// 失败方向必须朝"不附加"（退化成旧行为），绝不能朝"盲目附加"（未知参数会让 dsh 启动失败）。
        /// </summary>
        public static bool SupportsNoOpen(string dshVersion)
        {
            if (string.IsNullOrEmpty(dshVersion)) return false;
            if (!Semver.IsValid(dshVersion)) return false;
            return Semver.Compare(dshVersion, NoOpenSinceVersion) >= 0;
        }

        /// <summary>
        /// 由 dsh bin.js 路径（…\node_modules\@deepseek-ai\dsh\lib\bin.js）上溯两级到包根读 version。
        /// 纯本地文件读，不起子进程；路径不安全、包名不匹配或读取失败一律返回空串（调用方按"不支持"处理）。
        /// 刻意不缓存：本工具自己就能安装/更新 dsh，几毫秒的重读换来"没有缓存失效 bug"。
        /// </summary>
        public static string VersionFromBinJs(string binJs)
        {
            try
            {
                if (!PathGuard.IsSafe(binJs)) return "";
                var lib = Path.GetDirectoryName(binJs);   // …\@deepseek-ai\dsh\lib
                if (lib == null) return "";
                var root = Path.GetDirectoryName(lib);    // …\@deepseek-ai\dsh
                if (root == null) return "";
                var pkg = Path.Combine(root, "package.json");
                if (!File.Exists(pkg)) return "";
                var j = JObject.Parse(File.ReadAllText(pkg));
                if (!string.Equals((string)j["name"], DshPackageName, StringComparison.Ordinal)) return "";
                var v = (string)j["version"];
                return string.IsNullOrEmpty(v) ? "" : v;
            }
            catch { return ""; }
        }
    }
}
