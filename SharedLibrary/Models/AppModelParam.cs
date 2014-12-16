using System;
using System.Collections.Generic;

namespace SharedLibrary
{
	public class AppParam
	{
		public string AppId		{get;set;}
		public string AppName	{get;set;}
	}

	public class AppParamRoot
	{
		public List<AppParam> AppParams { get; set; }
	}
}

