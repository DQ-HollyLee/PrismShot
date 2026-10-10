// 照搬自 StarPie 的 WinPieGestures/AppConfig.cs —— 逐字未改。
// 插件不重新定义「OCR 该怎么配」，只是把宿主配置文件里的这一节读出来用：
// 用户在 StarPie 里选了哪个引擎、填了哪个 Key，插件必须用同一份，否则就是两个真相。

namespace StarPie.Plugin.PrismShot.Upstream;

public class OcrSettings
{
	/// <summary>OCR 服务提供商："Local" (Windows原生离线), "Ai" (OpenAI兼容多模态), "Cloud" (商业云端), "Custom" (自定义HTTP微服务)</summary>
	public string Provider { get; set; } = "Local";

	/// <summary>本地 OCR 首选语言（如 "zh-Hans", "zh-Hant", "en-US", "ja-JP"）</summary>
	public string LocalLanguage { get; set; } = "zh-Hans";

	/// <summary>AI 多模态大模型接口端点 (如 "https://api.openai.com/v1")</summary>
	public string AiEndpoint { get; set; } = "https://api.openai.com/v1";

	/// <summary>AI 多模态大模型密钥 (sk-...)</summary>
	public string AiApiKey { get; set; } = "";

	/// <summary>AI 模型名称 (如 "gpt-4o-mini", "Qwen/Qwen2.5-VL-72B-Instruct")</summary>
	public string AiModel { get; set; } = "gpt-4o-mini";

	/// <summary>AI 识别输出模式："text" (纯文字提取), "latex" (LaTeX公式), "markdown" (Markdown表格), "translate" (自动译为中文)</summary>
	public string AiPromptMode { get; set; } = "text";

	/// <summary>商业云端 OCR 服务商："Baidu", "Tencent", "Aliyun"</summary>
	public string CloudProvider { get; set; } = "Baidu";

	public string CloudApiKey { get; set; } = "";

	public string CloudSecretKey { get; set; } = "";

	/// <summary>自定义私有化 HTTP OCR 微服务接口 URL (如 "http://127.0.0.1:1224/api/ocr")</summary>
	public string CustomHttpUrl { get; set; } = "http://127.0.0.1:1224/api/ocr";

	public string CustomHttpFormat { get; set; } = "base64";

	/// <summary>识别完成后是否自动复制到系统剪贴板</summary>
	public bool AutoCopyToClipboard { get; set; } = true;

	/// <summary>识别完成后是否弹出结果悬浮窗口</summary>
	public bool ShowResultWindow { get; set; } = true;

	/// <summary>识别完成后是否直接在默认浏览器中搜索</summary>
	public bool SearchInBrowser { get; set; } = false;

	/// <summary>自动合并断句段落</summary>
	public bool MergeLines { get; set; } = true;

	/// <summary>自动去除中文字符间多余空格</summary>
	public bool RemoveSpacesBetweenCjk { get; set; } = true;
}
