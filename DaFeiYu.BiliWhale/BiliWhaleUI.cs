using System;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace DaFeiYu.BiliWhale;

/// <summary>
/// B站养鱼 —— 配置页（手写 C# 组件，非 razor）。
///
/// 顶部：动作区（⑰-3「扫码登录」）；下方：框架自动生成的配置表单（DefaultUI）。
/// ⚠️ DefaultUI 必须渲染，否则那一大堆配置项在设置页里根本不显示（这是宿主的行为）。
/// 手法照 Azuma.ContextDedup 的「配置页动作」与 Mao.MemoryInject 的 DefaultUI 兜底：
///   · 直接用 EventCallback.Factory.Create + 原生元素，不用 @bind/@onclick 语法糖
///   · 配置项改的是基类给的 Configuration 活对象，落盘交给框架的「保存配置」按钮
/// </summary>
public class BiliWhaleUI : ModuleUIBase<BiliWhaleModule, BiliWhaleConfig>
{
    const string PrimaryBtn = "border:0;border-radius:8px;background:#00a1d6;color:#fff;padding:8px 18px;font-size:13px;cursor:pointer";
    const string PrimaryBtnOff = "border:0;border-radius:8px;background:#9fc9d8;color:#fff;padding:8px 18px;font-size:13px;cursor:not-allowed";
    // 次要动作（检查更新 / 导出诊断）：都是"看一眼"的只读动作，视觉上不抢扫码登录的主按钮
    const string GhostBtn = "border:1px solid rgba(0,161,214,0.45);border-radius:8px;background:#fff;color:#0b7fa8;padding:7px 16px;font-size:13px;cursor:pointer";
    const string GhostBtnOff = "border:1px solid rgba(0,0,0,0.12);border-radius:8px;background:#f3f5f7;color:#9aa4ad;padding:7px 16px;font-size:13px;cursor:not-allowed";

    bool qrBusy;
    string qrResult = "";
    bool opsBusy;
    string opsTitle = "";
    string opsResult = "";

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        try
        {
            BuildBody(b);
        }
        catch (Exception e)
        {
            // 兜底：动作区崩了也**绝不能**把下面的配置表单一起吞掉（DefaultUI 才是主人改配置的地方）
            int i = 0;
            b.AddContent(i++, "配置页渲染出错：" + e.Message);
            b.AddContent(i++, DefaultUI);
        }
    }

    void BuildBody(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "配置未加载");
            return;
        }

        int i = 0;

        // ── 说明 ──
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "font-size:13px;color:#3a4252;line-height:1.8;padding:2px 0 6px");
        b.AddContent(i++, "扫码登录：点一下会先去 B站只读接口验一次当前凭据 —— **账号在线就什么都不做**，"
                        + "只回一句「账号在线，无需扫码」（不生成图片、不打登录接口）。只有确实不在线时，"
                        + "才会把登录二维码画成一张 PNG 放到桌面（路径可配），你用手机 B站 App 扫一下即可；"
                        + "登录成功后凭据自动写入并同步进配置，二维码图片随即删掉。全程不回显任何凭据。");
        b.CloseElement();

        // ── 动作行 ──
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:flex;gap:10px;flex-wrap:wrap;align-items:center;margin:6px 0 10px");

        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "style", qrBusy ? PrimaryBtnOff : PrimaryBtn);
        b.AddAttribute(i++, "disabled", qrBusy);
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, (_) => DoQrLoginAsync()));
        b.AddContent(i++, qrBusy ? "扫码登录中…（等你手机确认）" : "扫码登录");
        b.CloseElement();

        // ⑰-7① 检查更新（只读）——同一行里再放两个"看一眼"的动作
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "style", opsBusy ? GhostBtnOff : GhostBtn);
        b.AddAttribute(i++, "disabled", opsBusy);
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, (_) =>
            RunOpAsync("检查更新", () => Module == null
                ? Task.FromResult("模块未就绪：先在角色里启用「B站养鱼」并激活角色，再来点这个按钮。")
                : Module.UiCheckUpdateAsync())));
        b.AddContent(i++, opsBusy && opsTitle == "检查更新" ? "检查中…" : "检查更新");
        b.CloseElement();

        // ⑰-7③ 导出诊断（脱敏文本，只显示不落盘）
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "style", opsBusy ? GhostBtnOff : GhostBtn);
        b.AddAttribute(i++, "disabled", opsBusy);
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, (_) =>
            RunOpAsync("导出诊断", () => Module == null
                ? Task.FromResult("模块未就绪：先在角色里启用「B站养鱼」并激活角色，再来点这个按钮。")
                : Module.UiDiagnoseAsync())));
        b.AddContent(i++, opsBusy && opsTitle == "导出诊断" ? "收集中…" : "导出诊断");
        b.CloseElement();

        // 顺带告诉她/他二维码会落在哪（只显示路径，不碰任何凭据）
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "font-size:12px;color:rgba(0,0,0,0.55)");
        b.AddContent(i++, QrPathHint());
        b.CloseElement();

        b.CloseElement();

        // ── 结果 ──
        if (!string.IsNullOrEmpty(qrResult))
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "margin:2px 0 12px; padding:10px 12px; border-radius:8px; "
                                       + "background:rgba(0,161,214,0.08); border:1px solid rgba(0,161,214,0.25); "
                                       + "font-size:12.5px; color:#2b3a44; line-height:1.75; white-space:pre-wrap");
            b.AddContent(i++, qrResult);
            b.CloseElement();
        }

        // 检查更新 / 导出诊断 的结果（两个动作共用这一个框；纯文本，等宽更易读）
        if (!string.IsNullOrEmpty(opsResult))
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "margin:2px 0 12px; padding:10px 12px; border-radius:8px; "
                                       + "background:rgba(0,0,0,0.035); border:1px solid rgba(0,0,0,0.10); "
                                       + "font-size:12px; color:#2b3a44; line-height:1.7; white-space:pre-wrap; "
                                       + "font-family:Consolas,Menlo,monospace; max-height:420px; overflow:auto");
            b.AddContent(i++, (opsTitle.Length > 0 ? "【" + opsTitle + "】\n" : "") + opsResult);
            b.CloseElement();
        }

        // ── 框架自动生成的配置表单（必须在） ──
        b.AddContent(i++, DefaultUI);
    }

    string QrPathHint()
    {
        var raw = (Configuration?.QrImagePath ?? "").Trim();
        return raw.Length > 0
            ? $"二维码将存到：{raw}（扫完/过期自动删除）"
            : "二维码将存到：桌面 bili-login-qr.png（可在下面「扫码登录·二维码路径」里改；扫完/过期自动删除）";
    }

    async Task DoQrLoginAsync()
    {
        if (qrBusy) return;
        qrBusy = true;
        qrResult = "";
        StateHasChanged();
        try
        {
            qrResult = Module == null
                ? "模块未就绪：先在角色里启用「B站养鱼」并激活角色，再来点这个按钮。"
                : await Module.UiQrLoginAsync();
        }
        catch (Exception e)
        {
            qrResult = "扫码登录异常：" + e.Message;
        }
        finally
        {
            qrBusy = false;
            StateHasChanged();
        }
    }

    /// <summary>检查更新 / 导出诊断 共用的跑法（同一时刻只跑一个；两个都是只读动作）。</summary>
    async Task RunOpAsync(string title, Func<Task<string>> run)
    {
        if (opsBusy) return;
        opsBusy = true;
        opsTitle = title;
        opsResult = "";
        StateHasChanged();
        try
        {
            opsResult = await run();
        }
        catch (Exception e)
        {
            opsResult = title + "异常：" + e.Message;
        }
        finally
        {
            opsBusy = false;
            StateHasChanged();
        }
    }
}