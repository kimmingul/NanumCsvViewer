using System.ComponentModel.Design.Serialization;
using System.Data;
using System.Diagnostics;
using System.Text;
using MiniExcelLibs;
using Newtonsoft.Json;

namespace NanumCsvViewer
{

    public partial class Form1 : Form
    {
        // Global Variables
        string filePath;
        string programName = "Nanum CSV Viewer";

        public Form1()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            this.Text = programName;
            this.toolStripStatusLabel1.Text = "";



            //openFileDialog1.Filter = "All File (*.*)|*.*|All Data File (*.xls;*.xlsx,*.csv,*.txt)|*.xls;*.xlsx;*.csv;*.txt|All Excel File (*.xls;*.xlsx)|*.xls;*.xlsx|Microsoft Excel 97-2003 Workbook (*.xls)|*.xls|Microsoft Excel Workbook (*.xlsx)|*.xlsx|Comma Separated Value File (*.csv)|*.csv|Text File (*.txt)|*.txt";
            openFileDialog1.Filter = "All File (*.*)|*.*|Comma Separated Value File (*.csv)|*.csv";
            openFileDialog1.FilterIndex = 2;
            openFileDialog1.RestoreDirectory = true;
            openFileDialog1.Multiselect = false;
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFileDialog1.FileName = null;

            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    filePath = openFileDialog1.FileName;
                    var fileName = Path.GetFileName(filePath);
                    string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

                    // Use ExcelDataReader
                    var stopwatch = new Stopwatch();
                    stopwatch.Start();
                    DataSet dataSet = Utils.ImportDataFile(filePath);
                    DataTable dt = dataSet.Tables[0];
                    stopwatch.Stop();
                    var elapsed_time = stopwatch.ElapsedMilliseconds;

                    // Grid View
                    string message = string.Format("The file '{0}' has been loaded. \n (Loading time is {1} milliseconds)", fileName, elapsed_time);
                    MessageBox.Show(message, "Information", MessageBoxButtons.OK);
                    advancedDataGridView1.DataSource = dt;
                    advancedDataGridView1.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Sunken;
                    advancedDataGridView1.ColumnHeadersVisible = true;
                    advancedDataGridViewSearchToolBar1.SetColumns(advancedDataGridView1.Columns);

                    this.Text = programName + "  :  " + fileName;
                    this.toolStripStatusLabel1.Text = "Rows : " + dt.Rows.Count.ToString() + ", Columns : " + dt.Columns.Count.ToString() + "   (Loading Time : " + elapsed_time + " milliseconds)";

                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void openfastToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFileDialog1.FileName = null;

            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    filePath = openFileDialog1.FileName;
                    var fileName = Path.GetFileName(filePath);
                    string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

                    // Use LINQ
                    var stopwatch = new Stopwatch();
                    stopwatch.Start();
                    DataTable dt = Utils.ReadCsv(filePath);
                    stopwatch.Stop();
                    var elapsed_time = stopwatch.ElapsedMilliseconds;

                    // Grid View
                    string message = string.Format("The file '{0}' has been loaded. \n (Loading time is {1} milliseconds)", fileName, elapsed_time);
                    MessageBox.Show(message, "Information", MessageBoxButtons.OK);
                    advancedDataGridView1.DataSource = dt;
                    advancedDataGridView1.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Sunken;
                    advancedDataGridView1.ColumnHeadersVisible = true;
                    advancedDataGridViewSearchToolBar1.SetColumns(advancedDataGridView1.Columns);

                    this.Text = programName + "  :  " + fileName;
                    this.toolStripStatusLabel1.Text = "Rows : " + dt.Rows.Count.ToString() + ", Columns : " + dt.Columns.Count.ToString() + "   (Loading Time : " + elapsed_time + " milliseconds)";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void openminiExcelToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFileDialog1.FileName = null;

            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    filePath = openFileDialog1.FileName;
                    var fileName = Path.GetFileName(filePath);
                    string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);


                    // Use MiniExcel : High Performance
                    var stopwatch = new Stopwatch();
                    stopwatch.Start();
                    var raws = MiniExcel.Query(filePath).ToList();
                    var json = JsonConvert.SerializeObject(raws);
                    DataTable dt = (DataTable)JsonConvert.DeserializeObject(json, typeof(DataTable));
                    stopwatch.Stop();
                    var elapsed_time = stopwatch.ElapsedMilliseconds;

                    // Grid View
                    string message = string.Format("The file '{0}' has been loaded. \n (Loading time is {1} milliseconds)", fileName, elapsed_time);
                    MessageBox.Show(message, "Information", MessageBoxButtons.OK);
                    advancedDataGridView1.DataSource = dt;
                    advancedDataGridView1.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Sunken;
                    advancedDataGridView1.ColumnHeadersVisible = true;
                    advancedDataGridViewSearchToolBar1.SetColumns(advancedDataGridView1.Columns);

                    this.Text = programName + "  :  " + fileName;
                    this.toolStripStatusLabel1.Text = "Rows : " + dt.Rows.Count.ToString() + ", Columns : " + dt.Columns.Count.ToString() + "   (Loading Time : " + elapsed_time + " milliseconds)";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void quitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }


        private void advancedDataGridViewSearchToolBar1_Search(object sender, Zuby.ADGV.AdvancedDataGridViewSearchToolBarSearchEventArgs e)
        {
            bool restartsearch = true;
            int startColumn = 0;
            int startRow = 0;
            if (!e.FromBegin)
            {
                bool endcol = advancedDataGridView1.CurrentCell.ColumnIndex + 1 >= advancedDataGridView1.ColumnCount;
                bool endrow = advancedDataGridView1.CurrentCell.RowIndex + 1 >= advancedDataGridView1.RowCount;

                if (endcol && endrow)
                {
                    startColumn = advancedDataGridView1.CurrentCell.ColumnIndex;
                    startRow = advancedDataGridView1.CurrentCell.RowIndex;
                }
                else
                {
                    startColumn = endcol ? 0 : advancedDataGridView1.CurrentCell.ColumnIndex + 1;
                    startRow = advancedDataGridView1.CurrentCell.RowIndex + (endcol ? 1 : 0);
                }
            }
            DataGridViewCell c = advancedDataGridView1.FindCell(
                e.ValueToSearch,
                e.ColumnToSearch != null ? e.ColumnToSearch.Name : null,
                startRow,
                startColumn,
                e.WholeWord,
                e.CaseSensitive);
            if (c == null && restartsearch)
                c = advancedDataGridView1.FindCell(
                    e.ValueToSearch,
                    e.ColumnToSearch != null ? e.ColumnToSearch.Name : null,
                    0,
                    0,
                    e.WholeWord,
                    e.CaseSensitive);
            if (c != null)
                advancedDataGridView1.CurrentCell = c;
        }

        private void clearFilterToolStripMenuItem_Click(object sender, EventArgs e)
        {
            advancedDataGridView1.CleanFilter();
        }

        private void clearSortToolStripMenuItem_Click(object sender, EventArgs e)
        {
            advancedDataGridView1.CleanSort();
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            About modalForm = new About();
            modalForm.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            modalForm.ShowDialog();
        }
    }
}
