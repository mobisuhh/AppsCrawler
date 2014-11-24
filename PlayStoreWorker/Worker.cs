using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BDC.BDCCommons;
using SharedLibrary;
using SharedLibrary.Models;
using SharedLibrary.MongoDB;
using WebUtilsLib;

namespace PlayStoreWorker
{
    class Worker
    {
		// MongoDB Wrapper
		private static MongoDBWrapper mongoDB = null;

		// TODO: make this configurable
		private static readonly int MAX_RETRIES = 2;

        /// <summary>
        /// Entry point of the worker piece of the process
        /// Notice that you can run as many workers as you want to in order to make the crawling faster
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            // Configuring Log Object Threshold
            LogWriter.Threshold = TLogEventLevel.Information;
            LogWriter.Info ("Worker Started");

            // Parser
            PlayStoreParser parser = new PlayStoreParser();

			// Randomizer
			Random rand = new Random();

            // Configuring MongoDB Wrapper
            mongoDB = new MongoDBWrapper();
            string fullServerAddress = String.Join(":", Consts.MONGO_SERVER, Consts.MONGO_PORT);
            mongoDB.ConfigureDatabase(Consts.MONGO_USER, Consts.MONGO_PASS, Consts.MONGO_AUTH_DB, fullServerAddress, Consts.MONGO_TIMEOUT, Consts.MONGO_DATABASE, Consts.MONGO_COLLECTION);

            // Creating Instance of Web Requests Server
            WebRequests server = new WebRequests ();
            
            QueuedApp app;

            // Retry Counter (Used for exponential wait increasing logic)
            int retryCounter = 0;

            // Iterating Over MongoDB Records while no document is found to be processed                
            while ((app = mongoDB.FindAndModify ()) != null)
            {
                try
                {
                    // Building APP URL
                    string appUrl = Consts.APP_URL_PREFIX + app.Url;

                    // Checking if this app is on the database already
                    if (mongoDB.AppProcessed(appUrl))
                    {
                        // Console Feedback, Comment this line to disable if you want to
                        Console.WriteLine("Duplicated App, skipped.");

                        // Delete it from the queue and continues the loop
						RemoveFromQueue(app.Url);
                        continue;
                    }

                    // Configuring server and Issuing Request
                    server.Headers.Add(Consts.ACCEPT_LANGUAGE);
                    server.Host = Consts.HOST;
                    server.Encoding = "utf-8";
                    server.EncodingDetection = WebRequests.CharsetDetection.DefaultCharset;
					//
					string response = server.Get (appUrl);

                    // Flag Indicating Success while processing and parsing this app
                    bool ProcessingWorked = true;

                    // Sanity Check
                    if (String.IsNullOrEmpty (response) || server.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        LogWriter.Info ("Error opening app page : " + appUrl);
                        ProcessingWorked = false;

                        // Renewing WebRequest Object to get rid of Cookies
                        server = new WebRequests ();

                        // Inc. retry counter
                        retryCounter++;

                        Console.WriteLine ("Retrying:" + retryCounter);

						int randomWaitingTime = rand.Next((int) Math.Pow(2,retryCounter), (int) Math.Pow(2,retryCounter+1));
						double waitTime = TimeSpan.FromSeconds (randomWaitingTime).TotalMilliseconds;

						//
						// After max. number of retries, request details of a predefined well known app 
						// and check whether the response is invalid
						if (retryCounter >= MAX_RETRIES)
						{
							Console.Write("Request details of the predefined app..");
							string responseTest = server.Get (Consts.APP_URL_PREFIX + Consts.WELLKNOWN_APP_URL);

							//
							// Verify whether app details of the predefined app can be requested
							// if not, wait for a longer period of time
							if (String.IsNullOrEmpty (responseTest) || server.StatusCode != System.Net.HttpStatusCode.OK)
							{
								Console.Write("Failure");
								// set the wait time in minutes
								waitTime = TimeSpan.FromMinutes (randomWaitingTime).TotalMilliseconds;
							}
							//
							// otherwise, remove the app url from the database queue (probably the app page has expired)
							else
							{
								Console.Write("Success");

								RemoveFromQueue(app.Url);

								// Reset the retry counter
								retryCounter = 0;
							}

							Console.WriteLine(" ");
						}

                        // Hiccup to avoid google blocking connections in case of heavy traffic from the same IP
						Console.WriteLine("Sleep for {0} seconds..", TimeSpan.FromMilliseconds(waitTime).TotalSeconds);
                        Thread.Sleep (Convert.ToInt32 (waitTime));
                    }
                    else
                    {
                        // Reseting retry counter
                        retryCounter = 0;

                        // Parsing Useful App Data
                        AppModel parsedApp = parser.ParseAppPage (response, appUrl);

                        // Inserting App into Mongo DB Database
                        if (!mongoDB.Insert<AppModel>(parsedApp))
                        {
                            ProcessingWorked = false;
                        }

                        // If the processing failed, do not remove the app from the database, instead, keep it and flag it as not busy 
                        // so that other workers can try to process it later
                        if (!ProcessingWorked)
                        {
                            mongoDB.ToggleBusyApp(app, false);
                        }
                        else // On the other hand, if processing worked, removes it from the database
                        {
                            // Console Feedback, Comment this line to disable if you want to
                            Console.WriteLine("Inserted App : " + parsedApp.Name);

							RemoveFromQueue(app.Url);
                        }

                        // Counters for console feedback only
                        int extraAppsCounter = 0, newExtraApps = 0;

                        // Parsing "Related Apps" and "More From Developer" Apps (URLS Only)
                        foreach (string extraAppUrl in parser.ParseExtraApps (response))
                        {
                            // Incrementing counter of extra apps
                            extraAppsCounter++;

                            // Assembling Full app Url to check with database
                            string fullExtraAppUrl = Consts.APP_URL_PREFIX + extraAppUrl;

                            // Checking if the app was either processed or queued to be processed already
                            if ((!mongoDB.AppProcessed (fullExtraAppUrl)) && (!mongoDB.IsAppOnQueue(extraAppUrl)))
                            {
                                // Incrementing counter of inserted apps
                                newExtraApps++;

                                // Adds it to the queue of apps to be processed
                                mongoDB.AddToQueue (extraAppUrl);
                            }
                        }

                        // Console Feedback
                        Console.WriteLine ("Queued " + newExtraApps + " / " + extraAppsCounter + " related apps");
                    }
                }
                catch (Exception ex)
                {
                    LogWriter.Error (ex);
                }
                finally
                {
                    try
                    {
                        // Toggles Busy status back to false
                        mongoDB.ToggleBusyApp(app, false);
                    }
                    catch (Exception ex)
                    {
                        // Toggle Busy App may raise an exception in case of lack of internet connection, so, i must use this
                        // "inner catch" to avoid it from happenning
                        LogWriter.Error (ex);
                    }
                }
            }
        }

		private static void RemoveFromQueue(string appUrl)
		{
			// Removing App from the database (this the app page may have expired)
			Console.WriteLine("Removing app from the database queue: {0}", appUrl);
			mongoDB.RemoveFromQueue (appUrl);
		}
    }
}
