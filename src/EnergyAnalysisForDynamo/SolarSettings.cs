using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using DSCore;
using DSCoreNodesUI;
using Dynamo.Models;
using Dynamo.Nodes;
using Dynamo.Utilities;
using ProtoCore.AST.AssociativeAST;
using RevitServices.Persistence;
using RevitServices.Transactions;
using Revit.Elements;
using Revit.GeometryConversion;
using Autodesk.DesignScript.Runtime;
using Autodesk.DesignScript.Interfaces;
using Autodesk.DesignScript.Geometry;
using System.Text.RegularExpressions;

using EnergyAnalysisForDynamo.Utilities;

namespace EnergyAnalysisForDynamo
{
    /// <summary>
    /// Exposes some of Vasari's solar settings.
    /// </summary>
    public static class SolarSettings
    {
        /// <summary>
        /// Set the azimuth and altitude of the sun in the current view.
        /// </summary>
        /// <param name="az">Azimuth value in degrees.  Should be between 0 and 360</param>
        /// <param name="alt">Altitude value in degrees.  Should be between 0 and 90</param>
        /// <returns>a dummy string for now - what should this return?</returns>
        public static string SetAzimuthAltitude(double az, double alt)
        {
            //local varaibles
            Document RvtDoc = DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument.Document;
            SunAndShadowSettings SASS = null;

            //defense
            if (az < 0 || az > 360)
            {
                throw new Exception("Az must be between 0 and 360");
            }
            if (alt < 0 || alt > 90)
            {
                throw new Exception("Alt must be between 0 and 90");
            }


            //get the sunAndShadowSettings object from the active view
            try
            {
                SASS = DocumentManager.Instance.CurrentUIDocument.ActiveView.SunAndShadowSettings;
                if (SASS == null) throw new Exception();
            }
            catch (Exception ex)
            {
                throw new Exception("Couldn't get the SunAndShadowSettings from the active view.");
            }
            
            
            //make sure we are in a transaction
            TransactionManager.Instance.EnsureInTransaction(RvtDoc);

            //enable the lighting mode in the active view
            SASS.SunAndShadowType = SunAndShadowType.Lighting;
            SASS.RelativeToView = false;

            //set the azimuth and altitude
            SASS.Altitude = alt * 0.0174532925;
            SASS.Azimuth = az * 0.0174532925;

            //fit to model
            SASS.FitToModel();

            //hide the sun path
            DocumentManager.Instance.CurrentUIDocument.ActiveView.get_Parameter(BuiltInParameter.VIEW_GRAPH_SUN_PATH).Set(0);

            //done with the transaction
            TransactionManager.Instance.TransactionTaskDone();
            //DocumentManager.Regenerate();

            //in another transaction, show the sun, and refresh the view
            TransactionManager.Instance.EnsureInTransaction(RvtDoc);
            DocumentManager.Instance.CurrentUIDocument.ActiveView.get_Parameter(BuiltInParameter.VIEW_GRAPH_SUN_PATH).Set(1);
            DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument.RefreshActiveView();
            TransactionManager.Instance.TransactionTaskDone();
            //DocumentManager.Regenerate();

            return "success!";
        }
    }
}
