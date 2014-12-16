using MongoDB.Driver.Builders;
using Newtonsoft.Json;
using SharedLibrary;
using SharedLibrary.Models;
using SharedLibrary.MongoDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebUtilsLib;
using log4net;
using System.IO;
using log4net.Config;

namespace ReviewsParser
{
    /* 
     * ReviewsParser
     * This console application parses the reviews of a list of apps, either supplied via a json file, or retrieved
     * from the predefined database.
     */

    public class Program
    {
		// Input value constant denoting the usage of a database as input
		private static readonly string ARG_DATABASE = "database";
		private static readonly ILog log = LogManager.GetLogger(typeof(Program));
		// App URL Prefix (must be removed in order to obtain the app ID)
		private static readonly string URL_PLAYSTORE_IDPREFIX = "https://play.google.com/store/apps/details?id=";

        static void Main (string[] args)
        {
			// configure a basic console logger
			BasicConfigurator.Configure();

			log.Info ("Parsing arguments");
			var options = new ReviewParserOptions();
			IEnumerable<AppModel> appList = null;
			// Creating instance of Mongo Handler
			MongoDBWrapper mongoClient = new MongoDBWrapper ();

			if (CommandLine.Parser.Default.ParseArguments (args, options)) {
				log.InfoFormat ("Configuring Mongo Client");

				string fullServerAddress = String.Join (":", Consts.MONGO_SERVER, Consts.MONGO_PORT);
				mongoClient.ConfigureDatabase (Consts.MONGO_USER, Consts.MONGO_PASS, Consts.MONGO_AUTH_DB, fullServerAddress, Consts.MONGO_TIMEOUT, Consts.MONGO_DATABASE, Consts.MONGO_COLLECTION);

				//
				// Use file as input
				if (!options.Input.Equals (ARG_DATABASE)) {
					log.InfoFormat ("Reading AppIds from the file {0}", options.Input);

					try {
						var appModelRoot = JsonHelpers.CreateFromJsonFile<AppParamRoot> (options.Input);
						appList = CreateAppModelList (appModelRoot.AppParams);
					} catch (Exception e) {
						log.Error (e.Message);
						return;
					}
				//
				// .. or a database
				} else {
					log.Info ("Retrieving AppIds from the database");

					// Building MongoDB Query - This query specifies which applications you want to parse out the reviews
					// For more regarding MongoDB Queries, check the documentation on the project wiki page
					//var mongoQuery = Query.EQ ("Instalations", "1,000,000 - 5,000,000");
					var mongoQuery = Query.EQ ("Category", "/store/apps/category/MUSIC_AND_AUDIO");
					appList = mongoClient.FindMatch<AppModel> (mongoQuery, options.MaxApps, options.AppsToSkip);
				}
			} 
			else 
			{
				return;
			}

            // Creating Play Store Parser
            PlayStoreParser parser = new PlayStoreParser ();

            // Iterating over Query Results for the App Ids
			foreach (var app in appList)
            {
                // Extracting app ID from URL
				string appId = app.Url.Replace(URL_PLAYSTORE_IDPREFIX, String.Empty);

                // Console Feedback
				log.Info("Processing App [ " + app.Name + " ] ");

                bool shouldSkipApp = false;

                // Iterating over Review Pages up to the max received as argument
				for (int currentPage = 1; currentPage <= options.MaxReviewsPerApp; currentPage++)
                {
                    // Checking for the need to skip this app in case of duplicated review
                    if (shouldSkipApp)
                        break;

                    try
                    {
                        // Page Feedback
                        log.Info("Current Page: " + currentPage);

                        // Issuing Request for Reviews
                        string response = GetAppReviews(appId, currentPage);

                        // Checking for Blocking Situation
                        if (String.IsNullOrEmpty(response))
                        {
                            log.Info("Blocked by Play Store. Sleeping process for 10 minutes before retrying.");

                            // Thread Wait for 10 Minutes
                            Thread.Sleep(10 * 60 * 1000);
                        }

                        // Checking for "No Reviews" app
                        if (response.Length < 50)
                        {
                            log.Info("No Reviews for this app. Skipping");
							//TODO: Log the date and time of last try
                            break;
                        }

                        // Normalizing Response to Proper HTML
                        response = NormalizeResponse(response);

                        // Iterating over Parsed Reviews
                        foreach (var review in parser.ParseReviews(response))
                        {
                            // Adding App Data to the review
                            review.appID = appId;
							review.appName = app.Name;
							review.appURL = app.Url;

                            // Adding processing timestamp to the model
                            review.timestamp = DateTime.Now;

                            // Building Query to check for duplicated review
                            var duplicatedReviewQuery = Query.EQ("permalink", review.permalink);

                            // Checking for duplicated review before inserting it
                            if (mongoClient.FindMatch<AppReview>(duplicatedReviewQuery, 1, 0, Consts.REVIEWS_COLLECTION).Count() == 0)
                            {
                                // Inserting Review into MongoDB
                                mongoClient.Insert<AppReview>(review, Consts.REVIEWS_COLLECTION);
                            }
                            else
                            {
                                log.Info("Duplicated Review. Review already parsed. Skipping App");
                                //shouldSkipApp = true;
                                //break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex);
                    }
                }
            }
        }

        private static string GetAppReviews (string appID, int reviewsPage)
        {
            // Creating Instance of HTTP Requests Handler
            using (WebRequests httpClient = new WebRequests ())
            {
                // Configuring Request Object
                httpClient.Host              = Consts.HOST;
                httpClient.Origin            = Consts.ORIGIN;
                httpClient.Encoding          = "utf-8";
                httpClient.AllowAutoRedirect = true;
                httpClient.Accept            = "*/*";
                httpClient.UserAgent         = Consts.USER_AGENT;
                httpClient.ContentType       = "application/x-www-form-urlencoded;charset=UTF-8";
                httpClient.EncodingDetection = WebRequests.CharsetDetection.DefaultCharset;
                httpClient.Headers.Add (Consts.ACCEPT_LANGUAGE);

                // Assembling Post Data
                string postData = String.Format (Consts.REVIEWS_POST_DATA, reviewsPage, appID);

                // Issuing Request
                return httpClient.Post (Consts.REVIEWS_URL, postData);
            }
        }

        private static string NormalizeResponse (string jsonResponse)
        {
            // Replacing invalid characters with valid ones to ensure HTML correct formation
            string validHTML = jsonResponse.Replace ("\\u003c", "<").Replace ("\\u003d", "=").Replace ("\\u003e", ">")
                                           .Replace ("\\u0026amp;", "&").Replace (@"\""", @"""");

            // Removing HTML Garbage
            validHTML = validHTML.Substring  (validHTML.IndexOf ("<div class="));

            return validHTML;
        }

		private static List<AppModel> CreateAppModelList(List<AppParam> appParamList)
		{
			var listToReturn = new List<AppModel> ();
			foreach (var appParam in appParamList) 
			{
				if (String.IsNullOrEmpty (appParam.AppId))
					throw new ArgumentException ("Invalid (Empty) AppId found.");

				var appModel = new AppModel ();

				appModel.Url = URL_PLAYSTORE_IDPREFIX + appParam.AppId;
				appModel.Name = appParam.AppName;

				listToReturn.Add(appModel);
			}

			return listToReturn;
		}

		/*
		 * TODO: Remove this 
		 */
		private static IEnumerable<AppModel> CreateCustomAppModels(string appsToProcessArg)
		{
			string[] appsToProcess = null;
			appsToProcess = appsToProcessArg.Split (';');

			AppModel appModel = null;
			List<AppModel> appList = new List<AppModel> ();
			foreach (string appId in appsToProcess) 
			{
				if (String.IsNullOrEmpty (appId))
					continue;

				appModel = new AppModel ();
				appModel.Url = appId;
				appModel.Name = appId;

				appList.Add (appModel);
			}

			return appList;
		}
    }
}
