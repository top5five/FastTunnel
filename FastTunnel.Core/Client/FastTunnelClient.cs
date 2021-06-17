﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Extensions;
using System.Timers;
using System.Threading;
using Microsoft.Extensions.Logging;
using FastTunnel.Core.Handlers.Client;
using Microsoft.Extensions.Configuration;
using FastTunnel.Core.Server;

namespace FastTunnel.Core.Client
{
    public class FastTunnelClient
    {
        Connecter _client;

        ILogger<FastTunnelClient> _logger;

        System.Timers.Timer timer_timeout;
        System.Timers.Timer timer_heart;

        double heartInterval = 10 * 1000; // 10 秒心跳
        public DateTime lastHeart;
        Thread th;

        int reTrySpan = 10 * 1000; // 登陆失败后重试间隔
        HttpRequestHandler _newCustomerHandler;
        NewSSHHandler _newSSHHandler;
        LogHandler _logHandler;
        ClientHeartHandler _clientHeartHandler;
        Func<Connecter> lastLogin;
        Message<LogInMassage> loginMsg;

        public IClientConfig ClientConfig { get; set; }

        public FastTunnelClient(
            ILogger<FastTunnelClient> logger,
            HttpRequestHandler newCustomerHandler,
            NewSSHHandler newSSHHandler, LogHandler logHandler,
            IConfiguration configuration,
            ClientHeartHandler clientHeartHandler)
        {
            _logger = logger;
            _newCustomerHandler = newCustomerHandler;
            _newSSHHandler = newSSHHandler;
            _logHandler = logHandler;
            _clientHeartHandler = clientHeartHandler;
            ClientConfig = configuration.Get<AppSettings>().ClientSettings;
            initailTimer();
        }

        private void initailTimer()
        {
            timer_heart = new System.Timers.Timer();
            timer_heart.AutoReset = false;
            timer_heart.Interval = heartInterval;
            timer_heart.Elapsed += HeartElapsed;

            timer_timeout = new System.Timers.Timer();
            timer_timeout.AutoReset = false;
            timer_timeout.Interval = heartInterval + heartInterval / 2;
            timer_timeout.Elapsed += TimeoutElapsed;
        }

        private void TimeoutElapsed(object sender, ElapsedEventArgs e)
        {
            timer_timeout.Enabled = false;

            try
            {
                var timer = sender as System.Timers.Timer;
                var span = (DateTime.Now - lastHeart).TotalMilliseconds;
                if (span > timer.Interval)
                {
                    _logger.LogDebug($"last heart recived {span / 1000}s ago");

                    // 重新登录
                    reConnectAsync().Wait();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
            finally
            {
                timer_timeout.Enabled = true;
            }
        }

        private async Task reConnectAsync()
        {
            Close();
            try
            {
                _logger.LogInformation("登录重试...");
                _client = lastLogin.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);

                Thread.Sleep(reTrySpan);
                await reConnectAsync();
            }

            LogSuccess(_client.Socket);
        }

        private void HeartElapsed(object sender, ElapsedEventArgs e)
        {
            timer_heart.Enabled = false;

            try
            {
                _client.Send(new Message<HeartMassage> { MessageType = MessageType.Heart, Content = null });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }
            finally
            {
                timer_heart.Enabled = true;
            }
        }

        /// <summary>
        /// 启动客户端
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="customLoginMsg">自定义登录信息，可进行扩展业务</param>
        public void Start(object customLoginMsg)
        {
            _logger.LogInformation("===== FastTunnel Client Start =====");

            loginMsg = new Message<LogInMassage>
            {
                MessageType = MessageType.C_LogIn,
                Content = new LogInMassage
                {
                    Webs = ClientConfig.Webs,
                    SSH = ClientConfig.SSH,
                    CustomInfo = customLoginMsg,
                },
            };

            lastLogin = login;

            try
            {
                _client = lastLogin.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);

                Thread.Sleep(reTrySpan);
                reConnectAsync().Wait();
                return;
            }

            LogSuccess(_client.Socket);
        }

        private Connecter login()
        {
            Connecter _client;
            _logger.LogInformation($"正在连接服务端 {ClientConfig.Server.ServerAddr}:{ClientConfig.Server.ServerPort}");

            try
            {
                // 连接到的目标IP
                _client = new Connecter(ClientConfig.Server.ServerAddr, ClientConfig.Server.ServerPort);
                _client.Connect();

                _logger.LogInformation("连接成功");
            }
            catch (Exception)
            {
                Thread.Sleep(5000);
                throw;
            }

            // 登录
            _client.Send(loginMsg);

            return _client;
        }

        void Close()
        {
            timer_heart.Stop();
            timer_timeout.Stop();

            try
            {
                if (_client != null && _client.Socket.Connected)
                {
                    _client.Socket.Shutdown(SocketShutdown.Both);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
            finally
            {
                if (_client != null)
                {
                    _client.Socket.Close();
                }

                _logger.LogDebug("已退出登录\n");
            }
        }

        private void LogSuccess(Socket socket)
        {
            _logger.LogDebug("通信已建立");

            lastHeart = DateTime.Now;

            // 心跳开始
            timer_heart.Start();
            timer_timeout.Start();

            //th = new Thread(ReceiveServer);
            //th.Start(socket);

            ReceiveServerV2(socket);
        }

        private void ReceiveServerV2(object obj)
        {
            var client = obj as Socket;
            new PipeHepler(client, ProceccLine).ProcessLinesAsync();
        }

        private bool ProceccLine(Socket socket, byte[] line)
        {
            var cmd = Encoding.UTF8.GetString(line);
            HandleServerRequest(cmd);
            return true;
        }

        private void ReceiveServer(object obj)
        {
            var client = obj as Socket;
            byte[] buffer = new byte[1024];

            string lastBuffer = string.Empty;
            int n = 0;

            while (true)
            {
                try
                {
                    n = client.Receive(buffer);
                    if (n == 0)
                    {
                        client.Shutdown(SocketShutdown.Both);
                        break;
                    }
                }
                /// <see cref="https://docs.microsoft.com/zh-cn/windows/win32/winsock/windows-sockets-error-codes-2"/>
                catch (SocketException socketEx)
                {
                    // Connection timed out.
                    if (socketEx.ErrorCode == 10060)
                    {
                        _logger.LogInformation("Connection timed out");
                    }
                    else
                    {
                        _logger.LogError(socketEx);
                    }

                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex);
                    break;
                }

                string words = Encoding.UTF8.GetString(buffer, 0, n);
                if (!string.IsNullOrEmpty(lastBuffer))
                {
                    words = lastBuffer + words;
                    lastBuffer = null;
                }

                var msgs = words.Split("\n");

                _logger.LogDebug("recive from server:" + words);

                try
                {
                    foreach (var item in msgs)
                    {
                        if (string.IsNullOrEmpty(item))
                            continue;

                        if (item.EndsWith("}"))
                        {
                            HandleServerRequest(item);
                        }
                        else
                        {
                            lastBuffer = item;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                    continue;
                }
            }

            _logger.LogInformation("stop receive from server");
        }

        private IClientHandler HandleServerRequest(string words)
        {
            var Msg = JsonConvert.DeserializeObject<Message<JObject>>(words);
            IClientHandler handler;
            switch (Msg.MessageType)
            {
                case MessageType.Heart:
                    handler = _clientHeartHandler;
                    break;
                case MessageType.S_NewCustomer:
                    handler = _newCustomerHandler;
                    break;
                case MessageType.S_NewSSH:
                    handler = _newSSHHandler;
                    break;
                case MessageType.Log:
                    handler = _logHandler;
                    break;
                default:
                    throw new Exception($"未处理的消息：{Msg.MessageType} {Msg.Content}");
            }

            handler.HandlerMsg(this, Msg);
            return handler;
        }
    }
}
