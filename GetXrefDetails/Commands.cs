using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(GetXrefDetails.Commands))]
[assembly: ExtensionApplication(null)]

namespace GetXrefDetails
{
    public class Commands
    {
        // Method to get XRef details including names and paths
        public static List<(
            string Name,
            string Path,
            string SavedPath,
            string Status,
            string Type,
            bool IsUnloaded,
            bool IsHost
        )> GetXrefDetails()
        {
            List<(
                string Name,
                string Path,
                string SavedPath,
                string Status,
                string Type,
                bool IsUnloaded,
                bool IsHost
            )> xrefDetails =
                new List<(
                    string Name,
                    string Path,
                    string SavedPath,
                    string Status,
                    string Type,
                    bool IsUnloaded,
                    bool IsHost
                )>();

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    using (XrefGraph xg = db.GetHostDwgXrefGraph(true))
                    {
                        // Include the host drawing details
                        XrefGraphNode hostNode = xg.HostDrawing;
                        string hostPath = db.OriginalFileName;
                        xrefDetails.Add(
                            (hostNode.Name, hostPath, hostPath, "Loaded", "Host", false, true)
                        );

                        for (int cnt = 1; cnt < xg.NumNodes; cnt++) // Start from 1 to skip the host drawing
                        {
                            XrefGraphNode xNode = xg.GetXrefNode(cnt) as XrefGraphNode;
                            if (xNode == null)
                            {
                                ed.WriteMessage($"\nNode at index {cnt} is null.");
                                continue;
                            }

                            string xrefStatus = xNode.XrefStatus.ToString();
                            string xrefPath = xNode.Database?.Filename ?? "Unknown";
                            string savedPath = "Unknown";
                            string xrefType = xNode.IsNested ? "Overlay" : "Attach"; // Determine type
                            bool isUnloaded = xNode.Database == null; // Check if unloaded

                            // Get the saved path from the BlockTableRecord
                            if (xNode.BlockTableRecordId != ObjectId.Null)
                            {
                                BlockTableRecord btr =
                                    tr.GetObject(xNode.BlockTableRecordId, OpenMode.ForRead)
                                    as BlockTableRecord;
                                if (btr != null)
                                {
                                    savedPath = btr.PathName;
                                }
                            }

                            xrefDetails.Add(
                                (
                                    xNode.Name,
                                    xrefPath,
                                    savedPath,
                                    xrefStatus,
                                    xrefType,
                                    isUnloaded,
                                    false
                                )
                            );
                        }
                    }

                    tr.Commit();
                }
                catch (System.Exception exp)
                {
                    ed.WriteMessage("Exception: " + exp.Message);
                }
            }

            return xrefDetails;
        }

        // Command to write XRef details to a text file
        [CommandMethod("GetXrefDetailsToFile", CommandFlags.Modal)]
        public static void GetXrefDetailsToFileCommand()
        {
            List<(
                string Name,
                string Path,
                string SavedPath,
                string Status,
                string Type,
                bool IsUnloaded,
                bool IsHost
            )> xrefDetails = GetXrefDetails();

            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
            Document doc = Application.DocumentManager.MdiActiveDocument; // Ensure 'doc' is declared here

            string outputFilePath = Path.Combine(Environment.CurrentDirectory, "outputFile.txt");
            try
            {
                using (StreamWriter writer = new StreamWriter(outputFilePath))
                {
                    if (xrefDetails.Count == 0)
                    {
                        writer.WriteLine("No XRefs found.");
                        ed.WriteMessage("\nNo XRefs found.");
                    }
                    else
                    {
                        foreach (var detail in xrefDetails)
                        {
                            if (detail.IsHost)
                            {
                                writer.WriteLine("Host Drawing Details:");
                            }
                            else
                            {
                                writer.WriteLine("XRef Details:");
                            }
                            writer.WriteLine(
                                $"Name: {detail.Name}, Path: {detail.Path}, Saved Path: {detail.SavedPath}, Status: {detail.Status}, Type: {detail.Type}, Unloaded: {detail.IsUnloaded}"
                            );
                            writer.WriteLine(); // Add an empty line for better readability
                        }
                    }
                }

                ed.WriteMessage($"\nXRef details have been written to: {outputFilePath}");
            }
            catch (System.Exception exp)
            {
                ed.WriteMessage("Exception: " + exp.Message);
            }
        }
    }
}
