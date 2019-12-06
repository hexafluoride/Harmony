using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Nancy.Owin;
using System;
using System.Collections.Generic;
using System.Text;

namespace Harmony
{
    public class Startup
    {
        public Startup(IHostingEnvironment env)
        {
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseOwin(x => x.UseNancy(opt => opt.Bootstrapper = new Bootstrapper()));
        }
    }
}
