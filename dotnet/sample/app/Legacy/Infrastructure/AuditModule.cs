using System;
using System.Web;

namespace Shop.Legacy.Infrastructure
{
    /// <summary>Gira per ogni richiesta: l'area Admin e' riservata al ruolo Admin anche oltre il web.config.</summary>
    public class AuditModule : IHttpModule
    {
        public void Init(HttpApplication context)
        {
            context.BeginRequest += OnBeginRequest;
        }

        private void OnBeginRequest(object sender, EventArgs e)
        {
            var ctx = HttpContext.Current;
            if (ctx.Request.Path.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase) && !ctx.User.IsInRole("Admin"))
                throw new HttpException(403, "Accesso riservato agli amministratori");
        }

        public void Dispose()
        {
        }
    }
}
