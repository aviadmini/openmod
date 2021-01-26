﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MoreLinq.Extensions;
using OpenMod.API;
using OpenMod.API.Ioc;
using OpenMod.API.Jobs;
using OpenMod.API.Persistence;
using OpenMod.Core.Helpers;
using OpenMod.Core.Ioc;

namespace OpenMod.Core.Jobs
{
    [ServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public class JobScheduler : IJobScheduler, IDisposable
    {
        private const string c_DataStoreKey = "autoexec";
        private readonly IRuntime m_Runtime;
        private readonly ILogger<JobScheduler> m_Logger;
        private readonly IDataStore m_DataStore;
        private readonly List<ITaskExecutor> m_JobExecutors;
        private readonly List<ScheduledJob> m_ScheduledJobs;
        private static bool s_RunRebootJobs = true;
        private ScheduledJobsFile m_File = null!;
        private bool m_Started;

        public JobScheduler(
            IRuntime runtime,
            ILogger<JobScheduler> logger,
            IDataStoreFactory dataStoreFactory,
            IOptions<JobExecutorOptions> options)
        {
            m_Runtime = runtime;
            m_Logger = logger;

            m_DataStore = dataStoreFactory.CreateDataStore(new DataStoreCreationParameters
            {
                Component = runtime,
                LogOnChange = true,
                WorkingDirectory = runtime.WorkingDirectory
            });

            AsyncHelper.RunSync(ReadJobsFileAsync);

            m_ScheduledJobs = new List<ScheduledJob>();
            m_DataStore.AddChangeWatcher(c_DataStoreKey, runtime, () =>
            {
                m_ScheduledJobs.Clear();
                AsyncHelper.RunSync(() => StartAsync(isFromFileChange: true));
            });

            m_JobExecutors = new List<ITaskExecutor>();
            foreach (var provider in options.Value.JobExecutorTypes)
            {
                m_JobExecutors.Add(
                    (ITaskExecutor) ActivatorUtilitiesEx.CreateInstance(runtime.LifetimeScope, provider));
            }
        }

        public Task StartAsync()
        {
            return StartAsync(isFromFileChange: false);
        }

        private async Task StartAsync(bool isFromFileChange)
        {
            if (m_Started && !isFromFileChange)
            {
                return;
            }

            await ReadJobsFileAsync();

            if (m_File.Jobs != null)
            {
                foreach (var job in m_File.Jobs.ToList())
                {
                    await ScheduleJobInternalAsync(job, execStartup: !isFromFileChange, execReboot: s_RunRebootJobs);
                }
            }

            s_RunRebootJobs = false;
            m_Started = true;
        }

        public async Task<ScheduledJob> ScheduleJobAsync(JobCreationParameters @params)
        {
            if (@params == null)
            {
                throw new ArgumentNullException(nameof(@params));
            }

            m_File.Jobs ??= new();
            if (m_File.Jobs.Any(d => d.Name?.Equals(@params.Name) ?? false))
            {
                throw new Exception($"A job with this name already exists: {@params.Name}");
            }

            var job = new ScheduledJob(@params.Name, @params.Task, @params.Args, @params.Schedule);
            m_File.Jobs.Add(job);

            await WriteJobsFileAsync();
            await ScheduleJobInternalAsync(job, execStartup: false, execReboot: false);
            return job;
        }

        public Task<ScheduledJob?> FindJobAsync(string name)
        {
            if (m_File.Jobs == null)
            {
                return Task.FromResult<ScheduledJob?>(null);
            }

            return Task.FromResult<ScheduledJob?>(m_File.Jobs.FirstOrDefault(d => d.Name?.Equals(name) ?? false));
        }

        public async Task<bool> RemoveJobAsync(string name)
        {
            var job = await FindJobAsync(name);
            if (job == null)
            {
                return false;
            }

            return await RemoveJobAsync(job);
        }

        public async Task<bool> RemoveJobAsync(ScheduledJob job)
        {
            bool MatchedJobs(ScheduledJob d) => d.Name?.Equals(job.Name, StringComparison.Ordinal) ?? false;

            m_ScheduledJobs.RemoveAll(MatchedJobs);
            job.Enabled = false;

            if (m_File.Jobs == null)
            {
                return false;
            }

            var found = m_File.Jobs.RemoveAll(MatchedJobs) > 0;
            if (found)
            {
                await WriteJobsFileAsync();
            }

            return found;
        }

        public Task<ICollection<ScheduledJob>> GetScheduledJobsAsync(bool includeDisabled)
        {
            if (m_File.Jobs == null)
            {
                return Task.FromResult<ICollection<ScheduledJob>>(new List<ScheduledJob>());
            }

            return Task.FromResult<ICollection<ScheduledJob>>(m_File.Jobs
                .Where(d => includeDisabled || (d.Enabled ?? true))
                .ToList());
        }

        private async Task ReadJobsFileAsync()
        {
            if (!await m_DataStore.ExistsAsync(c_DataStoreKey))
            {
                m_File = new ScheduledJobsFile();
            }
            else
            {
                m_File = await m_DataStore.LoadAsync<ScheduledJobsFile>(c_DataStoreKey)
                         ?? throw new InvalidOperationException("Failed to load jobs from autoexec.yaml!");

                m_File.Jobs ??= new();
                var previousCount = m_File.Jobs.Count;
                m_File.Jobs = m_File.Jobs.DistinctBy(d => d.Name).ToList();

                // Duplicate jobs removed; save
                if (m_File.Jobs.Count != previousCount)
                {
                    await WriteJobsFileAsync();
                }
            }
        }

        private async Task WriteJobsFileAsync()
        {
            m_File.Jobs ??= new();
            await m_DataStore.SaveAsync(c_DataStoreKey, m_File);
        }

        private async Task ScheduleJobInternalAsync(ScheduledJob job, bool execStartup, bool execReboot)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            if (!(job.Enabled ?? true))
            {
                return;
            }

            if (string.IsNullOrEmpty(job.Name))
            {
                m_Logger.LogError($"Job of type {job.Task} has no name set.");
                return;
            }

            if (string.IsNullOrEmpty(job.Task))
            {
                m_Logger.LogError($"Job \"{job.Name}\" has no task set.");
                return;
            }

            if (string.IsNullOrEmpty(job.Schedule))
            {
                m_Logger.LogError($"Job \"{job.Name}\" has no schedule set.");
                return;
            }

            if (job.Schedule!.Equals("@single_exec", StringComparison.OrdinalIgnoreCase))
            {
                await ExecuteJobAsync(job);
                await RemoveJobAsync(job);
                return;
            }

            if (job.Schedule.Equals("@reboot", StringComparison.OrdinalIgnoreCase))
            {
                if (execReboot)
                {
                    await ExecuteJobAsync(job);
                }

                return;
            }

            if (job.Schedule.Equals("@startup", StringComparison.OrdinalIgnoreCase))
            {
                if (execStartup)
                {
                    await ExecuteJobAsync(job);
                }

                return;
            }

            ScheduleCronJob(job);
        }

        private void ScheduleCronJob(ScheduledJob job)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            var timezone = TimeZoneInfo.Local;

            CronExpression expression;
            try
            {
                expression = CronExpression.Parse(job.Schedule);
            }
            catch (Exception ex)
            {
                m_Logger.LogError(ex, $"Invalid crontab syntax \"{job.Schedule}\" for job: {job.Name}");
                return;
            }

            var nextOccurence = expression.GetNextOccurrence(DateTimeOffset.Now, timezone);
            if (nextOccurence == null)
            {
                return;
            }

            var delay = nextOccurence.Value - DateTimeOffset.Now;
            if (delay.TotalMilliseconds <= 0)
            {
                return;
            }

            m_ScheduledJobs.Add(job);

            AsyncHelper.Schedule($"Execution of job \"{job.Name}\"", async () =>
            {
                await Task.Delay(delay);

                if (!(job.Enabled ?? true) || !m_ScheduledJobs.Contains(job) || !m_Runtime.IsComponentAlive)
                {
                    return;
                }

                await ExecuteJobAsync(job);
                ScheduleCronJob(job);
            });
        }

        private async Task ExecuteJobAsync(ScheduledJob job)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            var jobExecutor = m_JobExecutors.FirstOrDefault(d => d.SupportsType(job.Task!));
            if (jobExecutor == null)
            {
                m_Logger.LogError($"[{job.Name}] Unknown job type: {job.Task}");
                return;
            }

            try
            {
                await jobExecutor.ExecuteAsync(new JobTask(job.Name!, job.Task!,
                    job.Args ?? new Dictionary<string, object?>()));
            }
            catch (Exception ex)
            {
                m_Logger.LogError(ex, $"Job \"{job.Name}\" generated an exception.");
            }
        }

        public void Dispose()
        {
            m_ScheduledJobs?.Clear();
            m_JobExecutors.Clear();
        }
    }
}