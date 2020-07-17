// LCS Discord Bot
// V1.2 
// Evan Greavu
// CSVHelper.cs

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LCSDiscordBot
{
    class CSVHelper
    {
        /// <summary>
        /// Read a CSV file into a list of rows, each row containing one or more columns.
        /// </summary>
        public List<List<string>> ReadCsvFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException();
            List<List<string>> result = new List<List<string>>();
            using (StreamReader reader = new StreamReader(new FileStream(path, FileMode.Open)))
            {
                string content = reader.ReadToEnd();
                string[] lines = content.Split('\n');
                foreach (string line in lines)
                {
                    string[] columns = line.Split(',');
                    result.Add(columns.ToList());
                }
            };
            return result;
        }

        /// <summary>
        /// Create a CSV file from a list of rows, each row containing one or more columns.
        /// </summary>
        public void SaveToCsvFile(string path, IList<IList<object>> data)
        {
            using (StreamWriter writer = new StreamWriter(new FileStream(path, FileMode.Create)))
            {
                foreach (var row in data)
                {
                    int columnCount = row.Count();
                    for (int i = 0; i < columnCount; i++)
                    {
                        writer.Write(row[i].ToString());
                        if (i + 1 != columnCount)
                            writer.Write(',');
                        else
                            writer.Write('\n');
                    }
                }
            }
        }
    }
}
