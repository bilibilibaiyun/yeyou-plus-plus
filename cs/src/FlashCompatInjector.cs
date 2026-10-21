using CefSharp;
using CefSharp.Handler;
using System;
using System.IO;
using System.Text;

namespace YeyouPlusPlus
{
    /// <summary>
    /// Flash 兼容注入脚本：在 HTML 文档最早阶段伪造 navigator.plugins / navigator.mimeTypes，
    /// 让用 swfobject 检测 Flash 插件的网站（如百田·奥奇传说）判定 Flash 可用，从而正常加载 SWF。
    /// 真正的渲染仍由 Pepper Flash（pepflashplayer.dll）完成。
    /// </summary>
    internal static class FlashCompatScript
    {
        public const string Script = @"(function(){try{var mt={type:'application/x-shockwave-flash',suffixes:'swf',description:'Shockwave Flash',enabledPlugin:null};var fp={name:'Shockwave Flash',filename:'pepflashplayer.dll',description:'Shockwave Flash 34.0 r0',version:'34.0.0.330',length:1};mt.enabledPlugin=fp;function mk(arr,key,val){return new Proxy(arr,{get:function(t,k){if(typeof k==='symbol'){return t[k];}if(k===key){return val;}if(k==='length'){return t.length;}if(k==='item'){return function(i){return t[i]||null;};}if(k==='namedItem'){return function(n){return n===key?val:null;};}if(k==='refresh'){return function(){};}var i=+k;if(!isNaN(i)&&String(i)===k){return t[i];}return t[k];}});}var pl=mk([fp],'Shockwave Flash',fp);var mts=mk([mt],'application/x-shockwave-flash',mt);function set(name,val){try{Object.defineProperty(Navigator.prototype,name,{get:function(){return val;},configurable:true});return true;}catch(e){}try{Object.defineProperty(navigator,name,{get:function(){return val;},configurable:true});return true;}catch(e){return false;}}set('plugins',pl);set('mimeTypes',mts);}catch(e){}try{if(window.swfobject){swfobject.hasFlashPlayerVersion=function(){return true;};try{swfobject.getFlashPlayerVersion=function(){return{major:34,minor:0,release:0};};}catch(e){}}}catch(e){}})();
(function(){try{function convert(){if(!document.querySelectorAll){return;}var nodes=document.querySelectorAll('object[type=\'application/x-shockwave-flash\']');var list=Array.prototype.slice.call(nodes);for(var i=0;i<list.length;i++){var obj=list[i];if(!obj||!obj.parentNode){continue;}var params={};var ps=obj.getElementsByTagName('param');for(var j=0;j<ps.length;j++){var p=ps[j];var nm=p.getAttribute('name');if(nm){params[nm]=p.getAttribute('value')||'';}}var swf=params['movie']||obj.getAttribute('data')||obj.getAttribute('src')||'';if(!swf){continue;}var em=document.createElement('embed');em.setAttribute('type','application/x-shockwave-flash');em.setAttribute('src',swf);em.setAttribute('width',obj.getAttribute('width')||params['width']||'960');em.setAttribute('height',obj.getAttribute('height')||params['height']||'560');var name=obj.getAttribute('name');if(name){em.setAttribute('name',name);}var id=obj.getAttribute('id');if(id){em.setAttribute('id',id);}for(var k in params){if(k!=='movie'&&Object.prototype.hasOwnProperty.call(params,k)){em.setAttribute(k,params[k]);}}obj.parentNode.replaceChild(em,obj);}}function observe(){try{if(window.MutationObserver){new MutationObserver(function(){try{convert();}catch(e){}}).observe(document.documentElement,{childList:true,subtree:true});}}catch(e){}}function start(){var t=setInterval(function(){if(document.documentElement){clearInterval(t);try{convert();}catch(e){}observe();}},50);}if(document.addEventListener){document.addEventListener('DOMContentLoaded',function(){try{convert();}catch(e){}},false);window.addEventListener('load',function(){try{convert();}catch(e){}},false);}start();}catch(e){}})();";
    }

    /// <summary>
    /// 对 HTML 文档响应注入 Flash 兼容脚本的 ResourceRequestHandler。
    /// 在 OnResourceResponse 里判断 MimeType 是否为 text/html，是才注入，避免污染图片/JS/CSS 等资源。
    /// </summary>
    public class FlashCompatInjector : ResourceRequestHandler
    {
        private bool isHtml;

        protected override bool OnResourceResponse(IWebBrowser chromiumWebBrowser, IBrowser browser,
            IFrame frame, IRequest request, IResponse response)
        {
            var mime = response?.MimeType;
            isHtml = mime != null && mime.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0;
            return false;
        }

        protected override IResponseFilter GetResourceResponseFilter(IWebBrowser chromiumWebBrowser,
            IBrowser browser, IFrame frame, IRequest request, IResponse response)
        {
            return isHtml ? new ScriptInjectionFilter(FlashCompatScript.Script) : null;
        }
    }

    /// <summary>
    /// 在响应流最开头注入一段 &lt;script&gt; 的响应过滤器。
    /// 第一次 Filter 调用先写注入脚本（不消费输入），返回 NeedMoreData 让 CEF 再次调用；
    /// 之后每次调用原样透传输入字节。
    /// </summary>
    public class ScriptInjectionFilter : IResponseFilter
    {
        private readonly byte[] scriptBytes;
        private bool injected;

        public ScriptInjectionFilter(string script)
        {
            scriptBytes = Encoding.UTF8.GetBytes("<script>" + script + "</script>");
        }

        public bool InitFilter() => true;

        public FilterStatus Filter(Stream dataIn, out long dataInRead, Stream dataOut, out long dataOutWritten)
        {
            if (dataIn == null)
            {
                // 响应结束（flush）：若尚未注入（空 HTML），不注入。
                dataInRead = 0;
                dataOutWritten = 0;
                return FilterStatus.Done;
            }

            if (!injected)
            {
                // 第一次：只写注入脚本，不读输入，返回 NeedMoreData 让 CEF 下次再传同一批数据。
                injected = true;
                dataInRead = 0;
                dataOut.Write(scriptBytes, 0, scriptBytes.Length);
                dataOutWritten = scriptBytes.Length;
                return FilterStatus.NeedMoreData;
            }

            // 之后：原样透传。
            dataInRead = Math.Min(dataIn.Length, dataOut.Length);
            dataOutWritten = dataInRead;
            if (dataInRead > 0)
            {
                var buffer = new byte[dataInRead];
                dataIn.Read(buffer, 0, (int)dataInRead);
                dataOut.Write(buffer, 0, (int)dataInRead);
            }
            return dataInRead < dataIn.Length ? FilterStatus.NeedMoreData : FilterStatus.Done;
        }

        public void Dispose()
        {
            // 无需释放资源。
        }
    }
}
