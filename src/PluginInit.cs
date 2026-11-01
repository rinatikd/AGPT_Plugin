// =============================================================================
// AGPT/APPT Plugin v1.0 — точка входа (IExtensionApplication)
// =============================================================================

using System;
using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AGPT.PluginInit))]

namespace AGPT
{
    public class PluginInit : IExtensionApplication
    {
        public const string Version = "1.0.0";

        public void Initialize()
        {
            try
            {
                AcadApp.ShowAlertDialog(
                    "AGPT/APPT v" + Version + " loaded.\n\n" +
                    "АГПТ: AGPT_ALBUM, AGPT_ZONE, AGPT_CALC, AGPT_HYDRO2\n" +
                    "АППТ: APPT_ALBUM, APPT_ZONE, APPT_CALC");
            }
            catch
            {
                try { MessageBox.Show("AGPT/APPT v" + Version + " loaded.", "AGPT"); } catch { }
            }

            try
            {
                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                var ed = doc?.Editor;
                if (ed != null)
                {
                    ed.WriteMessage("\n*** AGPT/APPT v" + Version + " LOADED OK ***");
                    ed.WriteMessage("\n  --- АГПТ (газ) ---");
                    ed.WriteMessage("\n  AGPT_ALBUM  AGPT_ZONE / AGPT_ZONE_PLINE  AGPT_ZONE_LIST");
                    ed.WriteMessage("\n  AGPT_MODULE  AGPT_PIPE  AGPT_CALC  AGPT_HYDRO2  AGPT_AXO2");
                    ed.WriteMessage("\n  AGPT_FRAME  AGPT_FRAME_LOAD  — рамка A3+штамп");
                    ed.WriteMessage("\n  AGPT_UGO_LIST  AGPT_UGO_IMPORT  AGPT_UGO_IMPORT_ALL");
                    ed.WriteMessage("\n  Зона: Pline → S → H → имя → Vp; затем AGPT_CALC → AGPT_ALBUM");
                    ed.WriteMessage("\n  --- АППТ (порошок) ---");
                    ed.WriteMessage("\n  APPT_ALBUM  APPT_ZONE  APPT_CALC");
                    ed.WriteMessage("\n  APPT_SHEET_OD  APPT_SHEET_CALC  APPT_SHEET_SPEC");
                    ed.WriteMessage("\n");
                }
            }
            catch { }
        }

        public void Terminate() { }
    }
}
