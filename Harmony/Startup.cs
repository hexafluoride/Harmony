using Chordette;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Nancy.Owin;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Harmony
{
    public class Startup
    {
        public Startup(IHostingEnvironment env)
        {
        }

        public void Configure(IApplicationBuilder app)
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.Add("X-Harmony-Node", Program.Node.ID.ToUsefulString());
                    context.Response.Headers.Add("X-Harmony-Listen-Endpoint", Program.Node.ListenEndPoint.ToString());

                    if (!string.IsNullOrWhiteSpace(Program.Name))
                        context.Response.Headers.Add("X-Harmony-Name", Program.Name);

                    return Task.FromResult(0);
                });
                await nextMiddleware();
            }).UseOwin(x => x.UseNancy(opt => opt.Bootstrapper = new Bootstrapper()));
        }
    }
}
