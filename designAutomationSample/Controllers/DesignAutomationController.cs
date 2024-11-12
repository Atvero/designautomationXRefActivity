using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Autodesk.Forge;
using Autodesk.Forge.Client;
using Autodesk.Forge.DesignAutomation;
using Autodesk.Forge.DesignAutomation.Model;
using Autodesk.Forge.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json.Linq;

namespace designAutomationSample.Controllers
{
    [ApiController]
    public class DesignAutomationController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly IHubContext<DesignAutomationHub> _hubContext;
        private readonly DesignAutomationClient _designAutomation;

        public DesignAutomationController(
            IWebHostEnvironment env,
            IHubContext<DesignAutomationHub> hubContext,
            DesignAutomationClient api
        )
        {
            _designAutomation = api;
            _env = env;
            _hubContext = hubContext;
        }

        public string LocalBundlesFolder => Path.Combine(_env.WebRootPath, "bundles");

        public static string NickName => OAuthController.GetAppSetting("APS_CLIENT_ID");

        public static string Alias => "dev";

        private static PostCompleteS3UploadPayload _postCompleteS3UploadPayload;
        public static PostCompleteS3UploadPayload S3UploadPayload
        {
            get => _postCompleteS3UploadPayload;
            set => _postCompleteS3UploadPayload = value;
        }

        [HttpGet]
        [Route("api/appbundles")]
        public string[] GetLocalBundles()
        {
            return Directory
                .GetFiles(LocalBundlesFolder, "*.zip")
                .Select(Path.GetFileNameWithoutExtension)
                .ToArray();
        }

        [HttpGet]
        [Route("api/aps/designautomation/engines")]
        public async Task<List<string>> GetAvailableEngines()
        {
            List<string> allEngines = new List<string>();
            string paginationToken = null;
            while (true)
            {
                Page<string> engines = await _designAutomation.GetEnginesAsync(paginationToken);
                allEngines.AddRange(engines.Data);
                if (engines.PaginationToken == null)
                    break;
                paginationToken = engines.PaginationToken;
            }
            allEngines.Sort();
            return allEngines;
        }

        [HttpPost]
        [Route("api/aps/designautomation/appbundles")]
        public async Task<IActionResult> CreateAppBundle([FromBody] JObject appBundleSpecs)
        {
            string zipFileName = appBundleSpecs["zipFileName"].Value<string>();
            string engineName = appBundleSpecs["engine"].Value<string>();

            string appBundleName = zipFileName + "AppBundle";

            string packageZipPath = Path.Combine(LocalBundlesFolder, zipFileName + ".zip");
            if (!System.IO.File.Exists(packageZipPath))
                throw new Exception("Appbundle not found at " + packageZipPath);

            Page<string> appBundles = await _designAutomation.GetAppBundlesAsync();

            dynamic newAppVersion;
            string qualifiedAppBundleId = $"{NickName}.{appBundleName}+{Alias}";
            if (!appBundles.Data.Contains(qualifiedAppBundleId))
            {
                AppBundle appBundleSpec = new AppBundle()
                {
                    Package = appBundleName,
                    Engine = engineName,
                    Id = appBundleName,
                    Description = $"Description for {appBundleName}",
                };
                newAppVersion = await _designAutomation.CreateAppBundleAsync(appBundleSpec);
                if (newAppVersion == null)
                    throw new Exception("Cannot create new app");

                Alias aliasSpec = new Alias() { Id = Alias, Version = 1 };
                Alias newAlias = await _designAutomation.CreateAppBundleAliasAsync(
                    appBundleName,
                    aliasSpec
                );
            }
            else
            {
                AppBundle appBundleSpec = new AppBundle()
                {
                    Engine = engineName,
                    Description = appBundleName
                };
                newAppVersion = await _designAutomation.CreateAppBundleVersionAsync(
                    appBundleName,
                    appBundleSpec
                );
                if (newAppVersion == null)
                    throw new Exception("Cannot create new version");

                AliasPatch aliasSpec = new AliasPatch() { Version = newAppVersion.Version };
                Alias newAlias = await _designAutomation.ModifyAppBundleAliasAsync(
                    appBundleName,
                    Alias,
                    aliasSpec
                );
            }

            using (var client = new HttpClient())
            {
                using (var formData = new MultipartFormDataContent())
                {
                    foreach (var kv in newAppVersion.UploadParameters.FormData)
                    {
                        if (kv.Value != null)
                        {
                            formData.Add(new StringContent(kv.Value), kv.Key);
                        }
                    }
                    using (
                        var content = new StreamContent(
                            new FileStream(packageZipPath, FileMode.Open)
                        )
                    )
                    {
                        formData.Add(content, "file");
                        using (
                            var request = new HttpRequestMessage(
                                HttpMethod.Post,
                                newAppVersion.UploadParameters.EndpointURL
                            )
                            {
                                Content = formData
                            }
                        )
                        {
                            var response = await client.SendAsync(request);
                            response.EnsureSuccessStatusCode();
                        }
                    }
                }
            }

            return Ok(new { AppBundle = qualifiedAppBundleId, Version = newAppVersion.Version });
        }

        private dynamic EngineAttributes(string engine)
        {
            if (engine.Contains("3dsMax"))
                return new
                {
                    commandLine = "$(engine.path)\\3dsmaxbatch.exe -sceneFile \"$(args[inputFile].path)\" $(settings[script].path)",
                    extension = "max",
                    script = "da = dotNetClass(\"Autodesk.Forge.Sample.DesignAutomation.Max.RuntimeExecute\")\nda.ModifyWindowWidthHeight()\n"
                };
            if (engine.Contains("AutoCAD"))
                return new
                {
                    commandLine = "$(engine.path)\\accoreconsole.exe /i \"$(args[inputFile].path)\" /al \"$(appbundles[{0}].path)\" /s $(settings[script].path)",
                    extension = "dwg",
                    script = "(command \"GetXrefDetailsToFile\")\n"
                };
            if (engine.Contains("Inventor"))
                return new
                {
                    commandLine = "$(engine.path)\\inventorcoreconsole.exe /i \"$(args[inputFile].path)\" /al \"$(appbundles[{0}].path)\"",
                    extension = "ipt",
                    script = string.Empty
                };
            if (engine.Contains("Revit"))
                return new
                {
                    commandLine = "$(engine.path)\\revitcoreconsole.exe /i \"$(args[inputFile].path)\" /al \"$(appbundles[{0}].path)\"",
                    extension = "rvt",
                    script = string.Empty
                };
            throw new Exception("Invalid engine");
        }

        [HttpPost]
        [Route("api/aps/designautomation/activities")]
        public async Task<IActionResult> CreateActivity([FromBody] JObject activitySpecs)
        {
            string zipFileName = activitySpecs["zipFileName"].Value<string>();
            string engineName = activitySpecs["engine"].Value<string>();

            string appBundleName = zipFileName + "AppBundle";
            string activityName = zipFileName + "Activity";

            Page<string> activities = await _designAutomation.GetActivitiesAsync();
            string qualifiedActivityId = $"{NickName}.{activityName}+{Alias}";
            if (!activities.Data.Contains(qualifiedActivityId))
            {
                dynamic engineAttributes = EngineAttributes(engineName);
                string commandLine = string.Format(engineAttributes.commandLine, appBundleName);
                Activity activitySpec = new Activity()
                {
                    Id = activityName,
                    Appbundles = new List<string> { $"{NickName}.{appBundleName}+{Alias}" },
                    CommandLine = new List<string> { commandLine },
                    Engine = engineName,
                    Parameters = new Dictionary<string, Parameter>
                    {
                        {
                            "inputFile",
                            new Parameter
                            {
                                Description = "input file",
                                LocalName = "$(inputFile)",
                                Ondemand = false,
                                Required = true,
                                Verb = Verb.Get,
                                Zip = false
                            }
                        },
                        {
                            "outputFile",
                            new Parameter
                            {
                                Description = "output file",
                                LocalName = "outputFile.txt",
                                Ondemand = false,
                                Required = true,
                                Verb = Verb.Put,
                                Zip = false
                            }
                        }
                    },
                    Settings = new Dictionary<string, ISetting>
                    {
                        {
                            "script",
                            new StringSetting { Value = engineAttributes.script }
                        }
                    }
                };
                Activity newActivity = await _designAutomation.CreateActivityAsync(activitySpec);

                Alias aliasSpec = new Alias { Id = Alias, Version = 1 };
                Alias newAlias = await _designAutomation.CreateActivityAliasAsync(
                    activityName,
                    aliasSpec
                );

                return Ok(new { Activity = qualifiedActivityId });
            }

            return Ok(new { Activity = "Activity already defined" });
        }

        [HttpGet]
        [Route("api/aps/designautomation/activities")]
        public async Task<List<string>> GetDefinedActivities()
        {
            Page<string> activities = await _designAutomation.GetActivitiesAsync();
            List<string> definedActivities = new List<string>();
            foreach (string activity in activities.Data)
                if (activity.StartsWith(NickName) && activity.IndexOf("$LATEST") == -1)
                    definedActivities.Add(activity.Replace(NickName + ".", string.Empty));

            return definedActivities;
        }

        static void onUploadProgress(float progress, TimeSpan elapsed, List<UploadItemDesc> objects)
        {
            Console.WriteLine(
                "progress: {0} elapsed: {1} objects: {2}",
                progress,
                elapsed,
                string.Join(", ", objects)
            );
        }

        public static async Task<string?> GetObjectId(
            string bucketKey,
            string objectKey,
            dynamic oauth,
            string fileSavePath
        )
        {
            try
            {
                ObjectsApi objectsApi = new ObjectsApi();
                objectsApi.Configuration.AccessToken = oauth.access_token;
                List<UploadItemDesc> uploadRes = await objectsApi.uploadResources(
                    bucketKey,
                    new List<UploadItemDesc>
                    {
                        new UploadItemDesc(
                            objectKey,
                            await System.IO.File.ReadAllBytesAsync(fileSavePath)
                        )
                    },
                    null,
                    onUploadProgress,
                    null
                );
                Console.WriteLine("**** Upload object(s) response(s):");
                DynamicDictionary objValues = uploadRes[0].completed;
                objValues.Dictionary.TryGetValue("objectId", out var id);

                return id?.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception when preparing input url:{ex.Message}");
                throw;
            }
        }

        [HttpPost]
        [Route("api/aps/designautomation/workitems")]
        public async Task<IActionResult> StartWorkitem([FromForm] StartWorkitemInput input)
        {
            JObject workItemData = JObject.Parse(input.data);
            string activityName = $"{NickName}.{workItemData["activityName"].Value<string>()}";
            string browserConnectionId = workItemData["browserConnectionId"].Value<string>();

            var fileSavePath = Path.Combine(
                _env.ContentRootPath,
                Path.GetFileName(input.inputFile.FileName)
            );
            using (var stream = new FileStream(fileSavePath, FileMode.Create))
            {
                await input.inputFile.CopyToAsync(stream);
            }

            dynamic oauth = await OAuthController.GetInternalAsync();

            string bucketKey = NickName.ToLower() + "-designautomation";
            BucketsApi buckets = new BucketsApi();
            buckets.Configuration.AccessToken = oauth.access_token;
            try
            {
                PostBucketsPayload bucketPayload = new PostBucketsPayload(
                    bucketKey,
                    null,
                    PostBucketsPayload.PolicyKeyEnum.Transient
                );
                await buckets.CreateBucketAsync(bucketPayload, "US");
            }
            catch
            { /* Bucket might already exist */
            }

            string inputFileNameOSS =
                $"{DateTime.Now:yyyyMMddhhmmss}_input_{Path.GetFileName(input.inputFile.FileName)}";
            string inputFileOSSUrl = await GetObjectId(
                bucketKey,
                inputFileNameOSS,
                oauth,
                fileSavePath
            );

            XrefTreeArgument inputFileArgument = new XrefTreeArgument
            {
                Url = inputFileOSSUrl,
                Headers = new Dictionary<string, string>
                {
                    { "Authorization", "Bearer " + oauth.access_token }
                }
            };

            // Use the provided SAS URL for the output file
            string outputFileSasUrl =
                "https://atveroclouddev.blob.core.windows.net/dwgcontainer/999888-PUR-00-00-DR-A-1000.txt?sp=racwd&st=2024-08-04T18:24:31Z&se=2024-08-05T02:24:31Z&sv=2022-11-02&sr=b&sig=qp4U6AkXM95GANYScpOrVvGfywJrPqKemDeqcFH596M%3D";

            XrefTreeArgument outputFileArgument = new XrefTreeArgument()
            {
                Url = outputFileSasUrl,
                Headers = new Dictionary<string, string>() { { "x-ms-blob-type", "BlockBlob" } },
                Verb = Verb.Put,
                LocalName = "outputFile.txt",
            };

            string outputFileNameOSS =
                $"{DateTime.Now:yyyyMMddhhmmss}_output_{Path.GetFileName(input.inputFile.FileName)}.txt";
            //string outputFileOSSUrl =
            //    $"https://developer.api.autodesk.com/oss/v2/buckets/{bucketKey}/objects/{outputFileNameOSS}";

            //XrefTreeArgument outputFileArgument = new XrefTreeArgument
            //{
            //    Url = outputFileOSSUrl,
            //    Headers = new Dictionary<string, string>
            //    {
            //        { "Authorization", "Bearer " + oauth.access_token }
            //    },
            //    Verb = Verb.Put,
            //    LocalName = "outputFile.txt",
            //};

            if (System.IO.File.Exists(fileSavePath))
            {
                System.IO.File.Delete(fileSavePath);
            }

            WorkItem workItemSpec = new WorkItem
            {
                ActivityId = activityName,
                Arguments = new Dictionary<string, IArgument>
                {
                    { "inputFile", inputFileArgument },
                    { "outputFile", outputFileArgument }
                }
            };
            WorkItemStatus workItemStatus = await _designAutomation.CreateWorkItemAsync(
                workItemSpec
            );
            await MonitorWorkitem(oauth, browserConnectionId, workItemStatus, outputFileNameOSS);
            return Ok(new { WorkItemId = workItemStatus.Id });
        }

        private async Task MonitorWorkitem(
            dynamic oauth,
            string browserConnectionId,
            WorkItemStatus workItemStatus,
            string outputFileNameOSS
        )
        {
            try
            {
                while (!workItemStatus.Status.IsDone())
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    workItemStatus = await _designAutomation.GetWorkitemStatusAsync(
                        workItemStatus.Id
                    );
                    await _hubContext
                        .Clients.Client(browserConnectionId)
                        .SendAsync("onComplete", workItemStatus.ToString());
                }
                using (var httpClient = new HttpClient())
                {
                    byte[] bs = await httpClient.GetByteArrayAsync(workItemStatus.ReportUrl);
                    string report = System.Text.Encoding.Default.GetString(bs);
                    await _hubContext
                        .Clients.Client(browserConnectionId)
                        .SendAsync("onComplete", report);
                }

                if (workItemStatus.Status == Status.Success)
                {
                    ObjectsApi objectsApi = new ObjectsApi();
                    objectsApi.Configuration.AccessToken = oauth.access_token;

                    ApiResponse<dynamic> res = await objectsApi.getS3DownloadURLAsyncWithHttpInfo(
                        NickName.ToLower() + "-designautomation",
                        outputFileNameOSS,
                        new Dictionary<string, object>
                        {
                            { "minutesExpiration", 15.0 },
                            { "useCdn", true }
                        }
                    );
                    await _hubContext
                        .Clients.Client(browserConnectionId)
                        .SendAsync("downloadResult", (string)(res.Data.url));
                    Console.WriteLine("Congrats!");
                }
            }
            catch (Exception ex)
            {
                await _hubContext
                    .Clients.Client(browserConnectionId)
                    .SendAsync("onComplete", ex.Message);
                Console.WriteLine(ex.Message);
            }
        }

        [HttpPost]
        [Route("/api/aps/callback/designautomation")]
        public async Task<IActionResult> OnCallback(
            string id,
            string outputFileName,
            [FromBody] dynamic body
        )
        {
            try
            {
                JObject bodyJson = JObject.Parse((string)body.ToString());
                await _hubContext.Clients.Client(id).SendAsync("onComplete", bodyJson.ToString());

                using (var httpClient = new HttpClient())
                {
                    byte[] bs = await httpClient.GetByteArrayAsync(
                        bodyJson["reportUrl"]?.Value<string>()
                    );
                    string report = System.Text.Encoding.Default.GetString(bs);
                    await _hubContext.Clients.Client(id).SendAsync("onComplete", report);
                }

                dynamic oauth = await OAuthController.GetInternalAsync();

                ObjectsApi objectsApi = new ObjectsApi();
                objectsApi.Configuration.AccessToken = oauth.access_token;

                ApiResponse<dynamic> res = await objectsApi.getS3DownloadURLAsyncWithHttpInfo(
                    NickName.ToLower() + "-designautomation",
                    outputFileName,
                    new Dictionary<string, object>
                    {
                        { "minutesExpiration", 15.0 },
                        { "useCdn", true }
                    }
                );
                await _hubContext
                    .Clients.Client(id)
                    .SendAsync("downloadResult", (string)(res.Data.url));
                Console.WriteLine("Congrats!");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            return Ok();
        }

        [HttpDelete]
        [Route("api/aps/designautomation/account")]
        public async Task<IActionResult> ClearAccount()
        {
            await _designAutomation.DeleteForgeAppAsync("me");
            return Ok();
        }

        public class StartWorkitemInput
        {
            public IFormFile inputFile { get; set; }
            public string data { get; set; }
        }
    }

    public class DesignAutomationHub : Microsoft.AspNetCore.SignalR.Hub
    {
        public string GetConnectionId()
        {
            return Context.ConnectionId;
        }
    }
}
