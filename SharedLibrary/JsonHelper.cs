using System;
using Newtonsoft.Json;
using System.IO;

namespace SharedLibrary
{
	/**
	 * Source: http://stackoverflow.com/a/17925665
	 */
	public static class JsonHelper
	{
		public static T CreateFromJsonStream<T>(this Stream stream)
		{
			JsonSerializer serializer = new JsonSerializer();
			T data;
			using (StreamReader streamReader = new StreamReader(stream))
			{
				data = (T)serializer.Deserialize(streamReader, typeof(T));
			}
			return data;
		}

		public static T CreateFromJsonString<T>(this String json)
		{
			T data;
			using (MemoryStream stream = new MemoryStream(System.Text.Encoding.Default.GetBytes(json)))
			{
				data = CreateFromJsonStream<T>(stream);
			}
			return data;
		}

		public static T CreateFromJsonFile<T>(this String fileName)
		{
			T data;
			using (FileStream fileStream = new FileStream(fileName, FileMode.Open))
			{
				data = CreateFromJsonStream<T>(fileStream);
			}
			return data;
		}
	}
}

