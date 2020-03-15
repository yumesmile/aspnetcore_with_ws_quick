using System;
using System.Net;
using System.Net.WebSockets;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

using Memory;

namespace server01 {
	public class Program {
		public static void Main(string[] args) {
			CreateHostBuilder(args).Build().Run();
		}

		public static IHostBuilder CreateHostBuilder(string[] args) =>
		  Host.CreateDefaultBuilder(args)
			.ConfigureWebHostDefaults(webBuilder => {
				webBuilder.ConfigureKestrel(serverOptions => {
					serverOptions.Limits.MaxConcurrentConnections = 100;
					serverOptions.Limits.MaxConcurrentUpgradedConnections = 100;
					serverOptions.Limits.MaxRequestBodySize = 10 * 1024;
					serverOptions.Limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
					serverOptions.Limits.MinResponseDataRate = new MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
					serverOptions.Listen(IPAddress.Loopback, 5000);
					serverOptions.Listen(IPAddress.Loopback, 5001, listenOptions => {
						listenOptions.UseHttps("testCert.pfx", "testPassword");
					});
					serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
					serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(1);
				})
			.UseStartup<MyStartup>();
			});
	}

	public class MyStartup {
		public MyStartup(IConfiguration configuration) {
			Configuration = configuration;
		}

		public IConfiguration Configuration { get; }

		// This method gets called by the runtime. Use this method to add services to the container.
		public void ConfigureServices(IServiceCollection services) {
			//services.AddRazorPages();
		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IWebHostEnvironment env) {
			if (env.IsDevelopment()) {
				app.UseDeveloperExceptionPage();
			} else {
				app.UseExceptionHandler("/Error");
				// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
				app.UseHsts();
			}

			//app.UseHttpsRedirection();
			var websocketOptions = new WebSocketOptions() {
				KeepAliveInterval = TimeSpan.FromSeconds(60),
				ReceiveBufferSize = 4 * 1024
			};
			app.UseWebSockets(websocketOptions);
			app.Use(async (context, next) => {
				if (context.Request.Path == "/ws" || context.WebSockets.IsWebSocketRequest) {
					if (context.WebSockets.IsWebSocketRequest) {

					} else {
						context.Response.StatusCode = 400;
					}
				} else {
					await next();
				}
			});

			app.UseStaticFiles();

			//app.UseRouting();
			//app.UseAuthorization();
			//app.UseEndpoints(endpoints => {
			//	endpoints.MapRazorPages();
			//});
		}

		void MyEntry(HttpContext context, WebSocket socket) {
			var rcver = new MsgReciver();
			//first packet;
			var reqPkt = rcver.Receive(context, socket);
			var msg = reqPkt.req;
			if(msg.AllBookGroup != null) {
				
			} else {
				//nack;
			}
			
			
			throw new NotImplementedException();
		}

		public void Configure2(IApplicationBuilder app) {
			var serverAddressesFeature = app.ServerFeatures.Get<IServerAddressesFeature>();

			app.UseStaticFiles();

			app.Run(async (context) => {
				context.Features.Get<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = 10 * 1024;

				var minRequestRateFeature = context.Features.Get<IHttpMinRequestBodyDataRateFeature>();
				var minResponseRateFeature = context.Features.Get<IHttpMinResponseDataRateFeature>();

				if (minRequestRateFeature != null) {
					minRequestRateFeature.MinDataRate = new MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
				}
				if (minResponseRateFeature != null) {
					minResponseRateFeature.MinDataRate = new MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
				}

				context.Response.ContentType = "text/html";
				await context.Response
				  .WriteAsync("<!DOCTYPE html><html lang=\"en\"><head>" +
					"<title></title></head><body><p>Hosted by Kestrel</p>");

				if (serverAddressesFeature != null) {
					await context.Response
					  .WriteAsync("<p>Listening on the following addresses: " +
						string.Join(", ", serverAddressesFeature.Addresses) +
						"</p>");
				}

				await context.Response.WriteAsync("<p>Request URL: " +
				  $"{context.Request.GetDisplayUrl()}<p>");
			});
		}
	}

	class MsgReciver {
		byte[] buf = new byte[1024 * 1024 * 4];

		ParsePacketMetaResult seqInfo;
		ParsePacketMetaResult dataPart;
		TimeSpan sleepspan;
		public MsgReciver() {
			seqInfo = new ParsePacketMetaResult() {
				nextOffset = 0,
				found = false,
				dsplen = 0,
				contnetLen = 0
			};
			dataPart = new ParsePacketMetaResult() {
				nextOffset = 0,
				found = false,
				dsplen = 0,
				contnetLen = 0
			};
			sleepspan = new TimeSpan(0, 0, 0, 0, 10);

		}

		public (SmsgClientReq req, byte[] data) Receive(HttpContext context, WebSocket socket) {
			//
			// msgPart
			//
			do {
				var t = receive_inner(socket, buf, seqInfo.nextOffset);
				t.Wait();
				seqInfo = t.Result;
			} while (!seqInfo.found && Sleep(new TimeSpan(0, 0, 0, 0, 10)));

			while (seqInfo.dsplen + seqInfo.contnetLen < seqInfo.nextOffset && Sleep(sleepspan)) {
				var t = receive_inner(socket, buf, seqInfo.nextOffset);
				t.Wait();
			}

			var msg = SmsgClientReq.Parser.ParseFrom(buf, seqInfo.dsplen, seqInfo.contnetLen);

			// preparing for datapart;
			var leftlen = seqInfo.nextOffset - seqInfo.dsplen - seqInfo.contnetLen;
			if (leftlen > 0) {
				//TODO: we need to cut off this logic for better performance in the future
				buf.CopyTo(buf, seqInfo.dsplen + seqInfo.contnetLen);
				seqInfo.nextOffset = leftlen;
			}
			seqInfo.found = false;
			seqInfo.dsplen = 0;
			seqInfo.contnetLen = 0;

			//
			// dataPart
			//
			do {
				var t = receive_inner(socket, buf, seqInfo.nextOffset);
				t.Wait();
				seqInfo = t.Result;
			} while (!seqInfo.found && Sleep(sleepspan));

			while (seqInfo.dsplen + seqInfo.contnetLen < seqInfo.nextOffset && Sleep(sleepspan)) {
				var t = receive_inner(socket, buf, seqInfo.nextOffset);
				t.Wait();
			}
			
			var data = new ArraySegment<byte>(buf, seqInfo.dsplen, seqInfo.contnetLen).ToArray();
			//preparing for next msgPart
			leftlen = seqInfo.nextOffset - seqInfo.dsplen - seqInfo.contnetLen;
			if(leftlen > 0) {
				buf.CopyTo(buf, seqInfo.dsplen + seqInfo.contnetLen);
				seqInfo.nextOffset = leftlen;
			}
			
			//build a result
			return (msg, data);
		}

		Task<ParsePacketMetaResult> receive_inner(WebSocket socket, byte[] ary, int offset) {
			return socket.ReceiveAsync(ary, CancellationToken.None).ContinueWith(t => {
				var receiveRes = t.Result;
				var rcvedCount = receiveRes.Count;
				ParsePacketMetaResult ret;
				ret.nextOffset = offset + rcvedCount;
				if (ret.nextOffset > 0) {
					ret = parseEachChunkOfPacket(ary, ret.nextOffset);
					return ret;
				} else {
					ret = new ParsePacketMetaResult() {
						found = false,
						dsplen = 0,
						contnetLen = 0
					};
				}
				return ret;
			});
		}

		bool Sleep(TimeSpan span) {
			for (var start = DateTime.Now; ;) {
				Thread.Sleep(span.Milliseconds);
				if (DateTime.Now - start >= span) {
					return true;
				}
			}
		}

		ParsePacketMetaResult parseEachChunkOfPacket(byte[] array, int arycount) {
			int len = arycount;
			if (len > 5) {
				len = 5;
			}
			ParsePacketMetaResult res = new ParsePacketMetaResult();
			for (int i = 0; i < len; i++) {
				res.contnetLen = (res.contnetLen << (i * 7)) | (buf[i] & 0x7F);
				if (buf[i] >= 0x80) {
					res.found = true;
					res.dsplen = i + 1;
					break;
				}
			}
			return res;
		}

		struct ParsePacketMetaResult {
			public int nextOffset;
			public bool found;
			public int dsplen;
			public int contnetLen;
		}
	}

	class MsgSender {
		
	}

}
