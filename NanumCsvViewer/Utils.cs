using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Data;
using ExcelDataReader;

class Utils
{
    public static DataTable ReadCsv(string filePath)
    {
        var dt = new DataTable();
        // 헤더 열 생성
        File.ReadLines(filePath)
            .Take(1)
            .SelectMany(line => line.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            .ToList()
            .ForEach(header => dt.Columns.Add(header.Trim()));

        // 데이터 행 추가
        File.ReadLines(filePath)
            .Skip(1)
            .Select(line => line.Split(','))
            .ToList()
            .ForEach(row => dt.Rows.Add(row));
        return dt;
    }

    public static DataSet ImportDataFile(string fName)
    {
        using (var stream = System.IO.File.Open(fName, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (Path.GetExtension(fName).ToUpper() == ".XLS" || Path.GetExtension(fName).ToUpper() == ".XLSX")
            {
                using (var reader = ExcelReaderFactory.CreateReader(stream))
                {
                    var result = reader.AsDataSet(new ExcelDataSetConfiguration()
                    {
                        ConfigureDataTable = (_) => new ExcelDataTableConfiguration()
                        {
                            EmptyColumnNamePrefix = "Column",
                            UseHeaderRow = true
                        }
                    });
                    DataSet fDataSet = result;
                    return fDataSet;
                }
            }
            else if (Path.GetExtension(fName).ToUpper() == ".CSV" || Path.GetExtension(fName).ToUpper() == ".TXT")
            {
                using (var reader = ExcelReaderFactory.CreateCsvReader(stream))  // this line is different to XLS code.
                {
                    var result = reader.AsDataSet(new ExcelDataSetConfiguration()
                    {
                        ConfigureDataTable = (_) => new ExcelDataTableConfiguration()
                        {
                            EmptyColumnNamePrefix = "Column",
                            UseHeaderRow = true
                        }
                    });
                    DataSet fDataSet = result;
                    return fDataSet;
                }
            }
        }
        DataSet e = new DataSet("0");
        return e;
    }

    // names : The Names of an DataTable
    public static string[] names(DataTable dt)
    {
        int n = dt.Columns.Count; // number of column
        string[] colname = new string[n];
        for (int i = 0; i < n; i++)
        {
            colname[i] = dt.Columns[i].ToString();
        }
        return colname;
    }

    // nrow : The Number of Rows of an Array
    public static int nrow<T>(T[,] a)
    { return a.GetLength(1); }
    public static int nrow(DataTable dt)
    { return dt.Rows.Count; }

    // unique : Extract Unique Elements to String Array  // 데이터테이블의 특정 컬럼 (index)의 unique 값을 string array로 반환해줌.
    public static string[] Unique(DataTable dt, int Index = 0)
    {
        string[] tmp = new string[dt.Rows.Count];
        for (int row = 0; row < dt.Rows.Count; row++)
        {
            tmp[row] = dt.Rows[row][Index].ToString();
        }
        string[] uniqueValue = tmp.Distinct().ToArray();
        return uniqueValue;
    }

    public static DataTable MakeDataTableColumnName(DataTable dtsource, int N)
    {
        // 데이터테이블의 N번째 행을 Column명 (header)로 만들어줌.
        DataTable dt = dtsource.Copy();
        for (int i = 0; i < dt.Columns.Count; i++)
        {
            dt.Columns[i].ColumnName = dt.Rows[N].ItemArray[i].ToString();
        }
        //
        for (int i = 0; i <= N; i++)
        {
            dt.Rows[i].Delete();
        }
        return dt;
    }

    public static string[,] DataTableToStringArray(DataTable dt)
    {
        // 데이터테이블을 2차원 string Array로 반환해줌.
        string[,] stringArray = new string[dt.Rows.Count, dt.Columns.Count];
        for (int row = 0; row < dt.Rows.Count; ++row)
        {
            for (int col = 0; col < dt.Columns.Count; col++)
            {
                stringArray[row, col] = dt.Rows[row][col].ToString();
            }
        }
        return stringArray;
    }

    public static string[] DataTableColumnNamesToStringArray(DataTable dt)
    {
        //데이터테이블의 컬럼명을 string Array로 반환해줌.
        string[] columnNames = dt.Columns.Cast<DataColumn>().Select(x => x.ColumnName).ToArray(); // Get all Column names of DataTable
        return columnNames;
    }

    // print : Print Values  (use with Console.WriteLine method.  for example, Console.WriteLine(print(a)) ).
    public static string print(DataTable dt, int header = 1)
    {
        // header = 1 -> Print header, header = 0 -> No print header
        int m = dt.Columns.Count; // number of column
        int n = dt.Rows.Count; // number of row

        string[] line = new string[m];
        string[] result = new string[n + header];
        if (header == 1)
        {
            result[0] = "\t" + String.Join("\t", names(dt));
        }
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < m; j++)
                line[j] = dt.Rows[i][j].ToString();
            result[i + header] = i + "\t" + String.Join("\t", line);
        }
        return String.Join("\n", result);
    }
    public static string print<T>(T[] a)
    { return "[0]\t" + String.Join("\t", a); }
    public static string print<T>(T[,] a)
    {
        int m = a.GetLength(0); // number of row
        int n = a.GetLength(1); // number of column

        string[] line = new string[n];
        string[] result = new string[m + 1];
        for (int j = 0; j < n; j++)
            line[j] = "[," + j + "]";
        result[0] = "\t" + String.Join("\t", line);
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < n; j++)
                line[j] = a[i, j].ToString();
            result[i + 1] = "[" + i + ",]\t" + String.Join("\t", line);
        }
        return String.Join("\n", result);
    }

}

