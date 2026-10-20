using Owin;

namespace Shop.Web
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            app.Use(new object());
        }
    }
}
