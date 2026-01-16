using Hangfire;
using Hangfire.Throttling;
using Smarties.Api.Extensions.Hangfire;
using Squid.Core.Jobs;
using Squid.Core.Services.Jobs;

namespace Squid.Api.Extensions.Hangfire;

public class InternalHangfireRegistrar : HangfireRegistrarBase
{
    public override void RegisterHangfire(IServiceCollection services, IConfiguration configuration)
    {
        base.RegisterHangfire(services, configuration);
    }

    public override void ApplyHangfire(IApplicationBuilder app, IConfiguration configuration)
    {
        base.ApplyHangfire(app, configuration);
        
        ScanHangfireRecurringJobs(app);

        app.UseHangfireDashboard(options: new DashboardOptions
        {
            IgnoreAntiforgeryToken = true
        });
        
        var manager = new ThrottlingManager();
    }
    
    private static void ScanHangfireRecurringJobs(IApplicationBuilder app)
    {
        var backgroundJobClient = app.ApplicationServices.GetRequiredService<ISquidBackgroundJobClient>();

        var recurringJobTypes = typeof(IRecurringJob).Assembly.GetTypes().Where(type => type.IsClass && typeof(IRecurringJob).IsAssignableFrom(type)).ToList();
        
        foreach (var type in recurringJobTypes)
        {
            var job = (IRecurringJob) app.ApplicationServices.GetRequiredService(type);

            if (string.IsNullOrEmpty(job.CronExpression))
            {
                Log.Error("Recurring Job Cron Expression Empty, {Job}", job.GetType().FullName);
                continue;
            }
            
            backgroundJobClient.AddOrUpdateRecurringJob<IJobSafeRunner>(job.JobId, r => r.Run(job.JobId, type), job.CronExpression, job.TimeZone);
        }
    }
}