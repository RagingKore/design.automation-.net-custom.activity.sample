﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.IO.Compression;

using AIO.Operations;
using AIO.ACES.Models;

namespace Client
{
    class Credentials
    {
        //get your ConsumerKey/ConsumerSecret at http://developer.autodesk.com
        public static string ConsumerKey = "";
        public static string ConsumerSecret = "";
    }
    class Program
    {

        static readonly string PackageName = "MyTestPackage";
        static readonly string ActivityName = "MyTestActivity";

        static Container container;
        static void Main(string[] args)
        {
            //instruct client side library to insert token as Authorization value into each request
            container = new Container(new Uri("https://developer.api.autodesk.com/autocad.io/us-east/v2/"));
            var token = GetToken();
            container.SendingRequest2 += (sender, e) => e.RequestMessage.SetHeader("Authorization", token);

            //check if our app package exists
            AppPackage package = null;
            var packageQ = container.AppPackages.ByKey(PackageName);
            try { package = packageQ.GetValue(); } catch { }
            string res = "New";
            if (package!=null)
            {
                res = PromptVersions(package.Version, packageQ.GetVersions().RequestUri, 
                    packageQ.SetVersion(0).RequestUri, token);
            }       
            if (res=="New")
                package = CreateOrUpdatePackage(CreateZip(), package);

            //check if our activity already exists
            Activity activity = null;
            var activityQ = container.Activities.ByKey(ActivityName);
            try { activity = activityQ.GetValue(); }
            catch { }
            res = "New";
            if (activity != null)
            {
                res = PromptVersions(package.Version, activityQ.GetVersions().RequestUri, 
                    activityQ.SetVersion(0).RequestUri, token);
                }
            if (res == "New")
                activity = CreateOrUpdateActivity(activity, PackageName);

            //save outstanding changes if any
            container.SaveChanges();

            //finally submit workitem against our activity
            SubmitWorkItem(activity);
        }

        static string GetToken()
        {
            Console.WriteLine("Getting authorization token...");
            using (var client = new HttpClient())
            {
                var values = new List<KeyValuePair<string, string>>();
                values.Add(new KeyValuePair<string, string>("client_id", Credentials.ConsumerKey));
                values.Add(new KeyValuePair<string, string>("client_secret", Credentials.ConsumerSecret));
                values.Add(new KeyValuePair<string, string>("grant_type", "client_credentials"));
                values.Add(new KeyValuePair<string, string>("scope", "code:all"));
                var requestContent = new FormUrlEncodedContent(values);
                var response = client.PostAsync("https://developer.api.autodesk.com/authentication/v1/authenticate", requestContent).Result;
                var responseContent = response.Content.ReadAsStringAsync().Result;
                var resValues = JsonConvert.DeserializeObject<Dictionary<string, string>>(responseContent);
                return resValues["token_type"] + " " + resValues["access_token"];
            }
        }

        static string PromptVersions(int curVer, Uri getVersions, Uri setVersion, string token)
        {
            // We have multiple versions of the is package already, retrieve all versions and show the current
            Console.WriteLine("'{0}' already exists. The following versions are available:", PackageName);
            int min = int.MaxValue;
            int max = int.MinValue;

            var cont = new Container(new Uri("https://developer.api.autodesk.com/autocad.io/us-east/v2/"));
            cont.SendingRequest2 += (sender, e) => e.RequestMessage.SetHeader("Authorization", token);

            foreach (dynamic item in cont.Execute<dynamic>(getVersions))
            {
                if (item.Version > max)
                    max = item.Version;
                if (item.Version < min)
                    min = item.Version;
                Console.WriteLine("Version #: {0}.Time Submitted: {1}.", item.Version, item.Timestamp);
            }
            Console.WriteLine("Current={0}", curVer);
            var res = Prompts.PromptForKeyword("What do you want to do? [New/SetCurrent/Leave]<New>");
            if (res == "SetCurrent")
            {
                var ver = Prompts.PromptForNumber("Choose a version", min, max);
                container.Execute(setVersion, "POST", new Microsoft.OData.Client.BodyOperationParameter("Version", ver));
            }
            return res;
        }
        static string CreateZip()
        {
            Console.WriteLine("Generating autoloader zip...");
            string zip = "package.zip";
            if (File.Exists(zip))
                File.Delete(zip);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                string bundle = PackageName + ".bundle";
                string name = "PackageContents.xml";
                archive.CreateEntryFromFile(name, Path.Combine(bundle, name));
                name = "CrxApp.dll";
                archive.CreateEntryFromFile(name, Path.Combine(bundle, "Contents", name));
                name = "Newtonsoft.Json.dll";
                archive.CreateEntryFromFile(name, Path.Combine(bundle, "Contents", name));
            }
            return zip;

        }

        static AppPackage CreateOrUpdatePackage(string zip, AppPackage package)
        {
            Console.WriteLine("Creating/Updating AppPackage...");
            // First step -- query for the url to upload the AppPackage file
            var url = container.AppPackages.GetUploadUrl().GetValue();

            // Second step -- upload AppPackage file
            UploadObject(url, zip);

            if (package == null)
            {
                // third step -- after upload, create the AppPackage entity
                package = new AppPackage()
                {
                    Id = PackageName,
                    RequiredEngineVersion = "21.0",
                    Resource = url
                };
                container.AddToAppPackages(package);
            }
            else
            {
                //or update the existing one with the new url
                package.Resource = url;
                container.UpdateObject(package);
            }
            container.SaveChanges();
            return package;
        }

        static void UploadObject(string url, string filePath)
        {
            Console.WriteLine("Uploading autoloader zip...");
            var client = new HttpClient();
            client.PutAsync(url, new StreamContent(File.OpenRead(filePath))).Result.EnsureSuccessStatusCode();
        }


        //creates an activity with 2 inputs and variable number of outputs. All outsputs are places
        //in a folder 'outputs'
        static Activity CreateOrUpdateActivity(Activity activity, string packageName)
        {
            Console.WriteLine("Creating/Updating Activity...");
            bool newlyCreated = false;
            if (activity == null)
            {
                activity = new Activity() { Id = ActivityName };
                newlyCreated = true;
            }
            activity.Instruction = new Instruction()
                {
                    Script = "_test params.json outputs\n"
                };
            activity.Parameters = new Parameters()
                {
                    InputParameters = {
                        new Parameter() { Name = "HostDwg", LocalFileName = "$(HostDwg)" },
                        new Parameter() { Name = "Params", LocalFileName = "params.json" },
                    },
                    OutputParameters = { new Parameter() { Name = "Results", LocalFileName = "outputs" } }
                };
            activity.RequiredEngineVersion = "21.0";
            if (newlyCreated)
            {
                activity.AppPackages.Add(packageName); // reference the custom AppPackage
                container.AddToActivities(activity);
            }
            else
                container.UpdateObject(activity);
            container.SaveChanges();
            return activity;
        }
        
        static void SubmitWorkItem(Activity activity)
        {
            Console.WriteLine("Submitting workitem...");
            //create a workitem
            var wi = new WorkItem()
            {
                Id = "", //must be set to empty
                Arguments = new Arguments(),
                ActivityId = activity.Id 
            };

            wi.Arguments.InputArguments.Add(new Argument()
            {
                Name = "HostDwg",// Must match the input parameter in activity
                Resource = "http://download.autodesk.com/us/samplefiles/acad/blocks_and_tables_-_imperial.dwg",
                StorageProvider = StorageProvider.Generic //Generic HTTP download (as opposed to A360)
            });
            wi.Arguments.InputArguments.Add(new Argument()
            {
                Name = "Params",// Must match the input parameter in activity
                ResourceKind = ResourceKind.Embedded, //use data URL to send json parameters without having to upload them to storage
                Resource = @"data:application/json, "+ JsonConvert.SerializeObject(new CrxApp.Parameters { ExtractBlockNames = true, ExtractLayerNames = true }),
                StorageProvider = StorageProvider.Generic //Generic HTTP download (as opposed to A360)
            });
            wi.Arguments.OutputArguments.Add(new Argument()
            {
                Name = "Results", //must match the output parameter in activity
                StorageProvider = StorageProvider.Generic, //Generic HTTP upload (as opposed to A360)
                HttpVerb = HttpVerbType.POST, //use HTTP POST when delivering result
                Resource = null, //use storage provided by AutoCAD.IO
                ResourceKind = ResourceKind.ZipPackage //upload files as zip package in output directory
            });

            container.AddToWorkItems(wi);
            container.SaveChanges();

            //polling loop
            do
            {
                Console.WriteLine("Sleeping for 2 sec...");
                System.Threading.Thread.Sleep(2000);
                container.LoadProperty(wi, "Status"); //http request is made here
                Console.WriteLine("WorkItem status: {0}", wi.Status);
            }
            while (wi.Status == ExecutionStatus.Pending || wi.Status == ExecutionStatus.InProgress);

            //re-query the service so that we can look at the details provided by the service
            container.MergeOption = Microsoft.OData.Client.MergeOption.OverwriteChanges;
            wi = container.WorkItems.ByKey(wi.Id).GetValue();

            //download the status report
            var url = wi.StatusDetails.Report;
            DownloadToDocs(url, @"AIO-report.txt");
            
            //Resource property of the output argument "Results" will have the output url
            url = wi.Arguments.OutputArguments.First(a => a.Name == "Results").Resource;
            DownloadToDocs(url, @"AIO.zip");

        }

        static void DownloadToDocs(string url, string localFile)
        {
            var client = new HttpClient();
            var content = (StreamContent)client.GetAsync(url).Result.Content;
            var fname = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), localFile);
            Console.WriteLine("Downloading to {0}.", fname);
            using (var output = System.IO.File.Create(fname))
            {
                content.ReadAsStreamAsync().Result.CopyTo(output);
                output.Close();
            }
        }

        static void DemoVersionControl()
        {
            // We have version control over submitted AppPackages/Activities. 
            // You can set current version by calling container.AppPackages.ByKey(PackageName).SetVersion(number).Execute();
            Console.WriteLine("We have version control over submitted AppPackages/Activities. Here is the version history of AppPackage with name \"{0}\":", PackageName);
            var appPackages = container.AppPackages.ByKey(PackageName).GetVersions().Execute();

            // send them to console 
            var verList = new List<int>();
            foreach (var app in appPackages)
            {
                verList.Add(app.Version);
                Console.WriteLine("Version #: {0}.  Time Submitted: {1}.", app.Version, app.Timestamp);
            }

            // set the current version to the earlest version
            verList.Sort();
            int firstVersion = verList.First();
            container.AppPackages.ByKey(PackageName).SetVersion(firstVersion).Execute();

            // check the current version is the first version
            var curAppPackage = container.AppPackages.ByKey(PackageName).GetValue();
            System.Diagnostics.Debug.Assert(firstVersion == curAppPackage.Version);
        }
    }
}
