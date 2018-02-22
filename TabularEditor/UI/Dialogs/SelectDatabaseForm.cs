﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.UI.Dialogs
{
    public partial class SelectDatabaseForm : Form
    {
        public SelectDatabaseForm()
        {
            InitializeComponent();
        }

        public static DialogResult Show(Server server)
        {
            var form = new SelectDatabaseForm();
            form.databasePage1.Server = server;
            var result = form.ShowDialog();
            DatabaseID = form.databasePage1.DatabaseID;

            return result;
        }
        
        public static string DatabaseID;

        private void SelectDatabaseForm_Shown(object sender, EventArgs e)
        {
        }

        private void databasePage1_Accept(object sender, EventArgs e)
        {
            btnOK.PerformClick();
        }

        private void databasePage1_Validation(object sender, ValidationEventArgs e)
        {
            btnOK.Enabled = e.IsValid;
        }
    }
}
