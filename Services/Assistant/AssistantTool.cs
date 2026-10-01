using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TopDock.Services
{
    /// <summary>
    /// 비서 도구 1개. 모델에게 보여줄 스키마(이름·설명·인자)와 실제 동작을 한 곳에 묶는다.
    /// 도구를 늘리려면 AssistantTools의 목록에 항목 하나만 추가하면 스키마·인자 검증·실행이 함께 반영된다.
    /// </summary>
    public sealed record AssistantTool(
        string Name,
        string Description,
        IReadOnlyDictionary<string, object?> Parameters,
        string[] Required,
        Func<JsonElement, CancellationToken, Task<string>> Run)
    {
        /// <summary>인자 하나를 표현한다 — Prop("integer", "0~100").</summary>
        public static Dictionary<string, object?> Prop(string type, string description)
            => new() { ["type"] = type, ["description"] = description };

        /// <summary>OpenAI 호환 function 스키마로 직렬화한다.</summary>
        public Dictionary<string, object?> ToSchema() => new()
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = Name,
                ["description"] = Description,
                ["parameters"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = Parameters,
                    ["required"] = Required,
                },
            },
        };
    }
}
