using Microsoft.AspNetCore.Components;

namespace Dashboard.Modules.WcsSimulator.Components;

/// <summary>模拟器页面基类：统一反馈提示与状态色。</summary>
public class PageBase : ComponentBase
{
    protected string _feedback = "";
    protected string _feedbackClass = "";

    protected void ShowFeedback(string message)
    {
        _feedback = message;
        _feedbackClass = message.StartsWith("❌") || message.Contains("失败") ? "error" : "success";
        StateHasChanged();
    }

    protected void ShowFeedback(string icon, string message)
    {
        _feedback = $"{icon} {message}";
        _feedbackClass = message.StartsWith("❌") || message.Contains("失败") ? "error" : "success";
        StateHasChanged();
    }
}