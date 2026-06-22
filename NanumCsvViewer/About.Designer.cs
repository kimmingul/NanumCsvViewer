namespace NanumCsvViewer
{
    partial class About
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(About));
            label1 = new Label();
            label2 = new Label();
            linkLabel1 = new LinkLabel();
            label3 = new Label();
            label4 = new Label();
            label5 = new Label();
            label6 = new Label();
            label7 = new Label();
            pictureBox1 = new PictureBox();
            button1 = new Button();
            ((System.ComponentModel.ISupportInitialize)pictureBox1).BeginInit();
            SuspendLayout();
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("맑은 고딕", 14.25F, FontStyle.Regular, GraphicsUnit.Point, 0);
            label1.ForeColor = Color.MidnightBlue;
            label1.Location = new Point(12, 19);
            label1.Name = "label1";
            label1.Size = new Size(189, 25);
            label1.TabIndex = 0;
            label1.Text = "Nanum CSV Viewer ";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(12, 127);
            label2.Name = "label2";
            label2.Size = new Size(150, 15);
            label2.TabIndex = 1;
            label2.Text = "(c) Nanum Space Co., Ltd.";
            // 
            // linkLabel1
            // 
            linkLabel1.AutoSize = true;
            linkLabel1.Location = new Point(186, 127);
            linkLabel1.Name = "linkLabel1";
            linkLabel1.Size = new Size(172, 15);
            linkLabel1.TabIndex = 2;
            linkLabel1.TabStop = true;
            linkLabel1.Text = "https://www.nanumspace.com";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.ForeColor = SystemColors.ControlDarkDark;
            label3.Location = new Point(161, 184);
            label3.Name = "label3";
            label3.Size = new Size(211, 15);
            label3.TabIndex = 3;
            label3.Text = "Developed by Min-Gul Kim, MD, PhD";
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(15, 151);
            label4.Name = "label4";
            label4.Size = new Size(106, 15);
            label4.TabIndex = 5;
            label4.Text = "All rights reserved.";
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Location = new Point(15, 108);
            label5.Name = "label5";
            label5.Size = new Size(159, 15);
            label5.TabIndex = 6;
            label5.Text = "Nanum CSV Viewer (64-bit)";
            // 
            // label6
            // 
            label6.AutoSize = true;
            label6.Location = new Point(15, 84);
            label6.Name = "label6";
            label6.Size = new Size(265, 15);
            label6.TabIndex = 7;
            label6.Text = "This Program is avalable for free use by anyone.";
            // 
            // label7
            // 
            label7.AutoSize = true;
            label7.ForeColor = SystemColors.ControlDarkDark;
            label7.Location = new Point(225, 208);
            label7.Name = "label7";
            label7.Size = new Size(138, 15);
            label7.TabIndex = 8;
            label7.Text = "2026-06-22, Happy day !";
            // 
            // pictureBox1
            // 
            pictureBox1.BackgroundImageLayout = ImageLayout.None;
            pictureBox1.Image = (Image)resources.GetObject("pictureBox1.Image");
            pictureBox1.Location = new Point(215, 12);
            pictureBox1.Name = "pictureBox1";
            pictureBox1.Size = new Size(157, 57);
            pictureBox1.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBox1.TabIndex = 9;
            pictureBox1.TabStop = false;
            // 
            // button1
            // 
            button1.Location = new Point(283, 231);
            button1.Name = "button1";
            button1.Size = new Size(75, 23);
            button1.TabIndex = 10;
            button1.Text = "OK";
            button1.UseVisualStyleBackColor = true;
            button1.Click += button1_Click;
            // 
            // About
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = SystemColors.Window;
            BackgroundImageLayout = ImageLayout.None;
            ClientSize = new Size(384, 261);
            Controls.Add(button1);
            Controls.Add(pictureBox1);
            Controls.Add(label7);
            Controls.Add(label6);
            Controls.Add(label5);
            Controls.Add(label4);
            Controls.Add(label3);
            Controls.Add(linkLabel1);
            Controls.Add(label2);
            Controls.Add(label1);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "About";
            ShowIcon = false;
            SizeGripStyle = SizeGripStyle.Show;
            Text = "Nanum CSV Viewer Information";
            TopMost = true;
            ((System.ComponentModel.ISupportInitialize)pictureBox1).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label label1;
        private Label label2;
        private LinkLabel linkLabel1;
        private Label label3;
        private Label label4;
        private Label label5;
        private Label label6;
        private Label label7;
        private PictureBox pictureBox1;
        private Button button1;
    }
}