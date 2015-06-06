﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Net;
using System.Windows.Threading;

// Serialization
using System.Runtime.Serialization;
//using System.Runtime.Serialization.Json;

//Autodesk
using Autodesk.Revit;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.DesignScript.Runtime;

//Dynamo
using DSCore;
using DSCoreNodesUI;
using Dynamo.Models;
using Dynamo.Nodes;
using Dynamo.Utilities;
using ProtoCore.AST.AssociativeAST;
using RevitServices.Persistence;
using RevitServices.Transactions;
using ProtoCore;
using ProtoCore.Utils;
using RevitServices.Elements;
using Dynamo;
using DynamoUtilities;

//Revit Services
using RevitServices;

//AuthHelper
using EnergyAnalysisforDynamoAuthHelper;

//Helper
using EnergyAnalysisForDynamo.Utilities;
using EnergyAnalysisForDynamo.DataContracts;

//DataContract
using Revit.Elements;
using System.Xml.Linq;
using System.Diagnostics;

namespace EnergyAnalysisForDynamo
{
    public static class RunAnalysis
    {

        // NODE: Create Base Run
        /// <summary>
        /// Uploads and runs the energy analysis at the cloud and returns 'RunId' for results. Will return 0 for output 'RunId' if the request times out, currenlty set 5 mins. GBS Project location information overwrites the gbxml file location. If gbXML locations are variant, create new Project for each.
        /// </summary>
        /// <param name="ProjectId"> Input Project ID </param>
        /// <param name="gbXMLPaths"> Input file path of gbXML File </param>
        /// <param name="ExecuteParametricRuns"> Set to true to execute parametric runs. You can read more about parametric runs here: http://autodesk.typepad.com/bpa/ </param>
        /// /// <param name="Timeout"> Set custom connection timeout value. Default is 300000 ms (2 mins) </param>
        /// <returns></returns>
        [MultiReturn("RunIds","UploadTimes","Report")]
        public static Dictionary<string, List<object>> RunEnergyAnalysis(int ProjectId, List<string> gbXMLPaths, bool ExecuteParametricRuns = false, int Timeout = 300000)
        {
            // Make sure the given file is an .xml
            foreach (var gbXMLPath in gbXMLPaths)
            {
                // check if it is exist
                if (!File.Exists(gbXMLPath))
                {
                    throw new Exception("The file doesn't exists!");
                }

                string extention = string.Empty;
                try
                {
                    extention = Path.GetExtension(gbXMLPath);
                }
                catch (Exception ex)
                {
                    throw new Exception(ex + "Use 'File Path' node to set the gbxml file location.");
                }

                if (extention != ".xml")
                {
                    throw new Exception("Make sure to input files are gbxml files");
                }

            }
           
		        // 1. Initiate the Revit Auth
                Helper.InitRevitAuthProvider();

                // 1.1 Turn off MassRuns
                try
                {
                    Helper._ExecuteMassRuns(ExecuteParametricRuns, ProjectId);
                }
                catch (Exception)
                { 
                    // Do Nothing!
                }

            //Output variables
            List<object> newRunIds = new List<object>();
            List<object> uploadTimes = new List<object>();
            List<object> Reports = new List<object>();

            foreach (var gbXMLPath in gbXMLPaths)
	        {
                int newRunId = 0;
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();

                // 2. Create A Base Run
                string requestCreateBaseRunUri = GBSUri.GBSAPIUri + string.Format(APIV1Uri.CreateBaseRunUri, "xml");
                HttpWebResponse response = null;
                try
                {
                    response =
                        (HttpWebResponse)
                        Helper._CallPostApi(requestCreateBaseRunUri, typeof(NewRunItem), Helper._GetNewRunItem(ProjectId, gbXMLPath),Timeout);
                }
                catch (Exception)
                {
                    string filename = Path.GetFileName(gbXMLPath);
                    newRunIds.Add("Couldot run the analysis for the file: " + filename + " Try run the Analysis for this file again! "); 
                }

                if (response != null)
                {
                    newRunId = Helper.DeserializeHttpWebResponse(response); 
                    newRunIds.Add(newRunId);
                    Reports.Add("Success!");
                }
                else
                {                  
                    newRunIds.Add(null);
                    // get file name
                    string filename = Path.GetFileName(gbXMLPath);
                    Reports.Add("Couldn't upload gbxml file name : " + filename + ". Set timeout longer and try to run again! ");
                    //throw new Exception("Couldot run the analysis for the file: " + filename );
                }

                stopwatch.Stop();
                uploadTimes.Add(stopwatch.Elapsed.ToString(@"m\:ss"));
	        }

            // 3. Populate the Outputs
            return new Dictionary<string,List<object>>
            {
                { "RunIds" , newRunIds},
                { "UploadTimes" , uploadTimes},
                { "Report" , Reports}
            };
        }


        // NODE: Create new Project
        /// <summary>
        /// Creates new project in GBS and returns new Project ID. Returns ProjectID if the project with same title already exists.
        /// </summary>
        /// <param name="ProjectTitle"> Title of the project </param>
        /// <returns></returns>
        [MultiReturn("ProjectId")]
        public static Dictionary<string, int> CreateProject(string ProjectTitle)
        {
            //1. Output variable
            int newProjectId = 0;

            //2. Initiate the Revit Auth
            Helper.InitRevitAuthProvider();

            //NOTE: GBS allows to duplicate Project Titles !!! from user point of view we would like keep Project Titles Unique.
            //Create Project node return the Id of a project if it already exists. If more than one project with the same name already exist, throw an exception telling the user that multiple projects with that name exist.

            //Check if the project exists
            List<Project> ExtngProjects = Helper.GetExistingProjectsTitles();

            var queryProjects = from pr in ExtngProjects
                                where pr.Title == ProjectTitle
                                select pr;

            if (queryProjects.Any()) // Existing Project
            {
                // check if multiple projects
                if (queryProjects.Count() > 1)
                {
                    // if there are multiple thow and exception
                    throw new Exception("Multiple Projects with this title " + ProjectTitle + " exist. Try with a another name or use GetProjectList Node to get the existing GBS projects' attributes");
                }
                else 
                {
                    newProjectId = queryProjects.First().Id;
                }
            }
            else //Create New Project
            { 
                #region Setup : Get values from current Revit document

                //local variable to get SiteLocation and Lat & Lon information
                Document RvtDoc = DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument.Document;

                //Load the default energy setting from the active Revit instance
                EnergyDataSettings myEnergySettings = Autodesk.Revit.DB.Analysis.EnergyDataSettings.GetFromDocument(RvtDoc);

                // get BuildingType and ScheduleId from document
                // Remap Revit enum/values to GBS enum/ values
                string RvtBldgtype = Enum.GetName(typeof(gbXMLBuildingType), myEnergySettings.BuildingType);
                int BuildingTypeId = Helper.RemapBldgType(RvtBldgtype);
                // this for comparison
                int RvtBuildingTypeId = (int)myEnergySettings.BuildingType;

                // Lets set the schedule ID to 1 for now
                //int ScheduleId = (int)myEnergySettings.BuildingOperatingSchedule;
                int ScheduleId = Helper.RemapScheduleType((int)myEnergySettings.BuildingOperatingSchedule);


                // Angles are in Rdaians when coming from revit API
                // Convert to lat & lon values 
                const double angleRatio = Math.PI / 180; // angle conversion factor

                double lat = RvtDoc.SiteLocation.Latitude / angleRatio;
                double lon = RvtDoc.SiteLocation.Longitude / angleRatio;

                #endregion

                #region Setup : Get default Utility Values

                //1. Initiate the Revit Auth
                Helper.InitRevitAuthProvider();

                // Try to get Default Utility Costs from API 
                string requestGetDefaultUtilityCost = GBSUri.GBSAPIUri + APIV1Uri.GetDefaultUtilityCost;
                string requestUriforUtilityCost = string.Format(requestGetDefaultUtilityCost, BuildingTypeId, lat, lon, "xml");
                HttpWebResponse responseUtility = (HttpWebResponse)Helper._CallGetApi(requestUriforUtilityCost);

                string theresponse = "";
                using (Stream responseStream = responseUtility.GetResponseStream())
                {
                    using (StreamReader streamReader = new StreamReader(responseStream))
                    {
                        theresponse = streamReader.ReadToEnd();
                    }
                }
                DefaultUtilityItem utilityCost = Helper.DataContractDeserialize<DefaultUtilityItem>(theresponse);

                #endregion

                // 2.  Create A New  Project
                string requestUri = GBSUri.GBSAPIUri + string.Format(APIV1Uri.CreateProjectUri, "xml");

                var response =
                    (HttpWebResponse)
                    Helper._CallPostApi(requestUri, typeof(NewProjectItem), Helper._CreateProjectItem(ProjectTitle, false, BuildingTypeId, ScheduleId, lat, lon, utilityCost.ElecCost, utilityCost.FuelCost));

                newProjectId = Helper.DeserializeHttpWebResponse(response);
            }

            // 3. Populate the Outputs
            return new Dictionary<string, int>
            {
                { "ProjectId", newProjectId}
            };
        }


        // NODE: Create gbXML from Mass
        /// <summary> 
        /// Create gbXML file from Mass and saves to a local location 
        /// </summary>
        /// <param name="FilePath"> Specify the file path location to save gbXML file </param>
        /// <param name="MassFamilyInstance"> Input Mass Id </param>
        /// <param name="MassShadingInstances"> Input Mass Ids for shading objects </param>
        /// <param name="Run"> Set Boolean True. Default is false </param>
        /// <returns name="report"> Success? </returns>
        /// <returns name="gbXMLPath"></returns>
        [MultiReturn("report", "gbXMLPath")]
        public static Dictionary<string, object> ExportMassToGBXML(string FilePath, AbstractFamilyInstance MassFamilyInstance, List<AbstractFamilyInstance> MassShadingInstances, Boolean Run = false)
        {
            // Local variables
            Boolean IsSuccess = false;
            string FileName = string.Empty;
            string Folder = string.Empty;

            // Check if path and directory valid
            if (System.String.IsNullOrEmpty(FilePath) || FilePath == "No file selected.")
            {
                throw new Exception("No File selected !");
            }

            FileName = Path.GetFileNameWithoutExtension(FilePath);
            Folder = Path.GetDirectoryName(FilePath);

            // Check if Directory Exists
            if (!Directory.Exists(Folder))
            {
                throw new Exception("Folder doesn't exist. Input valid Directory Path!");
            }
        

            //make RUN? inputs set to True mandatory
            if (Run == false)
            {
                throw new Exception("Set 'Connect' to True!");
            }

            //local variables
            Document RvtDoc = DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument.Document;

            //enable the analytical model in the document if it isn't already
            try
            {
                PrepareEnergyModel.ActivateEnergyModel(RvtDoc);
            }
            catch (Exception)
            {
                throw new Exception("Something went wrong when trying to enable the energy model.");
            }

            //get the id of the analytical model associated with that mass
            Autodesk.Revit.DB.ElementId myEnergyModelId = MassEnergyAnalyticalModel.GetMassEnergyAnalyticalModelIdForMassInstance(RvtDoc, MassFamilyInstance.InternalElement.Id);
            if (myEnergyModelId.IntegerValue == -1)
            {
                throw new Exception("Could not get the MassEnergyAnalyticalModel from the mass - make sure the Mass has at least one Mass Floor.");
            }
            MassEnergyAnalyticalModel mea = (MassEnergyAnalyticalModel)RvtDoc.GetElement(myEnergyModelId);
            ICollection<Autodesk.Revit.DB.ElementId> ZoneIds = mea.GetMassZoneIds();

            

            // get shading Ids
            List<Autodesk.Revit.DB.ElementId> ShadingIds = new List<Autodesk.Revit.DB.ElementId>();
            for (int i = 0; i < MassShadingInstances.Count(); i++)
            {

            // make sure input mass is valid as a shading
            if (MassInstanceUtils.GetMassLevelDataIds(RvtDoc, MassShadingInstances[i].InternalElement.Id).Count() > 0)
            {
                throw new Exception("Item " + i.ToString() + " in MassShadingInstances has mass floors assigned. Remove the mass floors and try again.");
            }

            ShadingIds.Add(MassShadingInstances[i].InternalElement.Id);
            }

            if (ShadingIds.Count != 0)
            {
                MassGBXMLExportOptions gbXmlExportOptions = new MassGBXMLExportOptions(ZoneIds.ToList(), ShadingIds); // two constructors 
                RvtDoc.Export(Folder, FileName, gbXmlExportOptions);

            }
            else
            {
                MassGBXMLExportOptions gbXmlExportOptions = new MassGBXMLExportOptions(ZoneIds.ToList()); // two constructors 
                RvtDoc.Export(Folder, FileName, gbXmlExportOptions);
            }
            

            // if the file exists return success message if not return failed message
            string path = Path.Combine(Folder, FileName + ".xml");

            if (System.IO.File.Exists(path))
            {
                // Modify the xml Program Info element, aithorize the
                XmlDocument doc = new XmlDocument();
                doc.Load(path);

                // EE: There must be a shorter way !
                XmlNode node = doc.DocumentElement;

                foreach (XmlNode node1 in node.ChildNodes)
                {
                    foreach (XmlNode node2 in node1.ChildNodes)
                    {
                        if (node2.Name == "ProgramInfo")
                        {
                            foreach (XmlNode childnode in node2.ChildNodes)
                            {
                                if (childnode.Name == "ProductName")
                                {
                                    string productname = "Dynamo _ " + childnode.InnerText;
                                    childnode.InnerText = productname;
                                }
                            }

                        }
                    }
                }

                //doc.DocumentElement.Attributes["ProgramInfo"].ChildNodes[1].Value += "Dynamo ";
                doc.Save(path);

                IsSuccess = true;
            }
            string message = "Failed to create gbXML file!";

            if (IsSuccess)
            {
                message = "Success! The gbXML file was created";
            }
            else
            {
                path = string.Empty;
            }

            // Populate Output Values
            return new Dictionary<string, object>
            {
                { "report", message},
                { "gbXMLPath", path} 
            };
        }


        // NODE: Create gbXML from Zones
        /// <summary>
        /// Exports gbXML file from Zones
        /// </summary>
        /// <param name="FilePath"> Specify the file path location to save gbXML file </param>
        /// <param name="ZoneIds"> Input Zone IDs</param>
        /// <param name="MassShadingInstances"> Input Mass Ids for shading objects </param>
        /// <param name="Run">Set Boolean True. Default is false </param>
        /// <returns name="report"> Success? </returns>
        /// <returns name="gbXMLPath"></returns>
        [MultiReturn("report", "gbXMLPath")]
        public static Dictionary<string, object> ExportZonesToGBXML(string FilePath, List<ElementId> ZoneIds, List<AbstractFamilyInstance> MassShadingInstances, Boolean Run = false)
        {
            // Local variables
            Boolean IsSuccess = false;
            string FileName = string.Empty;
            string Folder = string.Empty;

            // Check if path and directory valid
            if (System.String.IsNullOrEmpty(FilePath) || FilePath == "No file selected.")
            {
                throw new Exception("No File selected !");
            }

            FileName = Path.GetFileNameWithoutExtension(FilePath);
            Folder = Path.GetDirectoryName(FilePath);

            // Check if Directory Exists
            if (!Directory.Exists(Folder))
            {
                throw new Exception("Folder doesn't exist. Input valid Directory Path!");
            }

            //make RUN? inputs set to True mandatory
            if (Run == false)
            {
                throw new Exception("Set 'Connect' to True!");
            }

            //local varaibles
            Document RvtDoc = DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument.Document;

            //enable the analytical model in the document if it isn't already
            try
            {
                PrepareEnergyModel.ActivateEnergyModel(RvtDoc);
            }
            catch (Exception)
            {
                throw new Exception("Something went wrong when trying to enable the energy model.");
            }

            //convert the ElementId wrapper instances to actual Revit ElementId objects
            List<Autodesk.Revit.DB.ElementId> outZoneIds = ZoneIds.Select(e => new Autodesk.Revit.DB.ElementId(e.InternalId)).ToList();


            // get shading Ids
            List<Autodesk.Revit.DB.ElementId> ShadingIds = new List<Autodesk.Revit.DB.ElementId>();
            for (int i = 0; i < MassShadingInstances.Count(); i++)
            {

                // make sure input mass is valid as a shading
                if (MassInstanceUtils.GetMassLevelDataIds(RvtDoc, MassShadingInstances[i].InternalElement.Id).Count() > 0)
                {
                    throw new Exception("Item " + i.ToString() + " in MassShadingInstances has mass floors assigned. Remove the mass floors and try again.");
                }

                ShadingIds.Add(MassShadingInstances[i].InternalElement.Id);
            }

            if (ShadingIds.Count != 0)
            {
                // Create gbXML with shadings
                MassGBXMLExportOptions gbXmlExportOptions = new MassGBXMLExportOptions(outZoneIds.ToList(), ShadingIds); // two constructors 
                RvtDoc.Export(Folder, FileName, gbXmlExportOptions);

            }
            else
            {
                // Create gbXML
                MassGBXMLExportOptions gbXmlExportOptions = new MassGBXMLExportOptions(outZoneIds.ToList()); // two constructors 
                RvtDoc.Export(Folder, FileName, gbXmlExportOptions);
            }

            
            // if the file exists return success message if not return failed message
            string path = Path.Combine(Folder, FileName + ".xml");

            if (System.IO.File.Exists(path))
            {
                // Modify the xml Program Info element, aithorize the
                XmlDocument doc = new XmlDocument();
                doc.Load(path);

                // EE: There must be a shorter way !
                XmlNode node = doc.DocumentElement;
                foreach (XmlNode node1 in node.ChildNodes)
                {
                    foreach (XmlNode node2 in node1.ChildNodes)
                    {
                        if (node2.Name == "ProgramInfo")
                        {
                            foreach (XmlNode childnode in node2.ChildNodes)
                            {
                                if (childnode.Name == "ProductName")
                                {
                                    string productname = "Dynamo _ " + childnode.InnerText;
                                    childnode.InnerText = productname;
                                }
                            }
                        }
                    }
                }

                doc.Save(path);

                IsSuccess = true;
            }
            string message = "Failed to create gbXML file!";

            if (IsSuccess)
            {
                message = "Success! The gbXML file was created";
            }
            else
            {
                path = string.Empty;
            }


            return new Dictionary<string, object>
            {
                { "report", message},
                { "gbXMLPath", path} 
            };


        }
    }
}
