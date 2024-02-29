﻿using AntDesign;
using AntSK.Domain.Model;
using AntSK.Domain.Repositories;
using AntSK.Domain.Utils;
using Azure.AI.OpenAI;
using Azure.Core;
using DocumentFormat.OpenXml.EMMA;
using MarkdownSharp;
using Microsoft.AspNetCore.Components;
using Microsoft.KernelMemory;
using Microsoft.OpenApi.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Newtonsoft.Json;
using SqlSugar;
using System;
using System.Text;
using AntSK.Domain.Utils;

namespace AntSK.Pages.ChatPage
{
    public partial class OpenChat
    {
        [Parameter]
        public string AppId { get; set; }
        [Inject] 
        protected MessageService? Message { get; set; }
        [Inject]
        protected IApps_Repositories _apps_Repositories { get; set; }
        [Inject]
        protected IKmss_Repositories _kmss_Repositories { get; set; }
        [Inject]
        protected IKmsDetails_Repositories _kmsDetails_Repositories { get; set; }
        [Inject]
        protected MemoryServerless _memory { get; set; }
        [Inject]
        protected Kernel _kernel { get; set; }

        protected bool _loading = false;
        protected List<MessageInfo> MessageList = [];
        protected string? _messageInput;
        protected string _json = "";
        protected bool Sendding = false;

        protected Apps app = new Apps();
        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();
            app = _apps_Repositories.GetFirst(p=>p.Id==AppId);
        }
        protected async Task OnSendAsync()
        {
            if (string.IsNullOrWhiteSpace(_messageInput))
            {
                _ = Message.Info("请输入消息", 2);
                return;
            }

            MessageList.Add(new MessageInfo() { 
                ID=Guid.NewGuid().ToString(),
                Context=_messageInput,
                CreateTime=DateTime.Now,
                IsSend=true
            });


            Sendding = true;
            await SendAsync(_messageInput);
            _messageInput = "";
            Sendding = false;

        }
        protected async Task OnCopyAsync(MessageInfo item)
        {
            await Task.Run(() =>
            {
                _messageInput = item.Context;
            });
        }

        protected async Task OnClearAsync(string id)
        {
            await Task.Run(() =>
            {
                MessageList = MessageList.Where(w => w.ID != id).ToList();
            });
        }

        protected async Task<bool> SendAsync(string questions)
        {
            string msg = questions;
            //处理多轮会话
            if (MessageList.Count > 0)
            {
                msg = await HistorySummarize(questions);
            }

            Apps app=_apps_Repositories.GetFirst(p => p.Id == AppId);
            switch (app.Type)
            {
                case "chat":
                    //普通会话
                    await SendChat(questions, msg, app);
                    break;
                case "kms":
                    //知识库问答
                    await SendKms(questions, msg, app);
                    break;
            }

            return await Task.FromResult(true);
        }

        /// <summary>
        /// 发送知识库问答
        /// </summary>
        /// <param name="questions"></param>
        /// <param name="msg"></param>
        /// <param name="app"></param>
        /// <returns></returns>
        private async Task SendKms(string questions, string msg, Apps app)
        {
            //知识库问答
            var filters = new List<MemoryFilter>();

            var kmsidList = app.KmsIdList.Split(",");
            foreach (var kmsid in kmsidList)
            {
                filters.Add(new MemoryFilter().ByTag("kmsid", kmsid));
            }

            var kmsResult = await _memory.AskAsync(msg, index: "kms", filters: filters);
            if (kmsResult != null)
            {
                if (!string.IsNullOrEmpty(kmsResult.Result))
                {
                    string answers = kmsResult.Result;
                    var markdown = new Markdown();
                    string htmlAnswers = markdown.Transform(answers);
                    var info1 = new MessageInfo()
                    {
                        ID = Guid.NewGuid().ToString(),
                        Context = answers,
                        HtmlAnswers = htmlAnswers,
                        CreateTime = DateTime.Now,
                    };
                    MessageList.Add(info1);
                }
            }
        }

        /// <summary>
        /// 发送普通对话
        /// </summary>
        /// <param name="questions"></param>
        /// <param name="msg"></param>
        /// <param name="app"></param>
        /// <returns></returns>
        private async Task SendChat(string questions, string msg, Apps app)
        {
            if (string.IsNullOrEmpty(app.Prompt))
            {
                //如果模板为空，给默认提示词
                app.Prompt = "{{$input}}";
            }
            var promptTemplateFactory = new KernelPromptTemplateFactory();
            var promptTemplate = promptTemplateFactory.Create(new PromptTemplateConfig(app.Prompt));
            var renderedPrompt = await promptTemplate.RenderAsync(_kernel);

            var func = _kernel.CreateFunctionFromPrompt(app.Prompt, new OpenAIPromptExecutionSettings());
            var chatResult = _kernel.InvokeStreamingAsync<StreamingChatMessageContent>(function: func, arguments: new KernelArguments() { ["input"] = msg });
            MessageInfo info = null;
            var markdown = new Markdown();
            await foreach (var content in chatResult)
            {
                if (info == null)
                {
                    info = new MessageInfo();
                    info.ID = Guid.NewGuid().ToString();
                    info.Context = content?.Content?.ConvertToString(); 
                    info.HtmlAnswers = content?.Content?.ConvertToString();
                    info.CreateTime = DateTime.Now;

                    MessageList.Add(info);
                }
                else
                {
                    info.HtmlAnswers += content.Content;
                    await Task.Delay(50); 
                }
                await InvokeAsync(StateHasChanged);
            }
            //全部处理完后再处理一次Markdown
            info!.HtmlAnswers = markdown.Transform(info.HtmlAnswers);
            await InvokeAsync(StateHasChanged);
        }

        /// <summary>
        /// 历史会话的会话总结
        /// </summary>
        /// <param name="questions"></param>
        /// <returns></returns>
        private async Task<string> HistorySummarize(string questions)
        {
            StringBuilder history = new StringBuilder();
            foreach (var item in MessageList)
            {
                if (item.IsSend)
                {
                    history.Append($"user:{item.Context}{Environment.NewLine}");
                }
                else 
                {
                    history.Append($"assistant:{item.Context}{Environment.NewLine}");
                }    
            }

            KernelFunction sunFun = _kernel.Plugins.GetFunction("ConversationSummaryPlugin", "SummarizeConversation");
            var summary = await _kernel.InvokeAsync(sunFun, new() { ["input"] = $"内容是：{history.ToString()} {Environment.NewLine} 请注意用中文总结" });
            string his = summary.GetValue<string>();
            var msg = $"历史对话：{his}{Environment.NewLine} 用户问题：{Environment.NewLine}{questions}"; ;
            return msg;
        }
    }
}
