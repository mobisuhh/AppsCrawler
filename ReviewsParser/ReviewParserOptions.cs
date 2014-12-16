using System;
using CommandLine;
using CommandLine.Text;
using System.Text;

namespace ReviewsParser
{
	public class ReviewParserOptions
	{
		[Option('i', "input", Required = true, DefaultValue="database", HelpText = "Source type of AppIds ('database' or a concrete json file)")]
		public string Input { get; set; }

		[Option('a', "maxapps", DefaultValue = 10, HelpText = "The maximum number of apps to process.")]
		public int MaxApps { get; set; }

		[Option('r', "maxreviews", DefaultValue = 100, HelpText = "The maximum number of reviews per app to process.")]
		public int MaxReviewsPerApp { get; set; }

		[Option('s', "apps2skip", DefaultValue = 0, HelpText = "The maximum number of reviews per app to process.")]
		public int AppsToSkip { get; set; }

		[HelpOption]
		public string Help()
		{
			return HelpText.AutoBuild(this,
				(HelpText current) => HelpText.DefaultParsingErrorsHandler(this, current));
		}
	}
}

