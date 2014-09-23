﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.DesignScript.Runtime;



namespace EnergyAnalysisForDynamo
{
    
    internal static class SingleSignOnManager
    {
        /// <summary>
        ///     A reference to the the SSONET assembly to prevent reloading.
        /// </summary>
        private static Assembly singleSignOnAssembly;

        /// <summary>
        ///     Delay loading of the SSONet.dll
        /// </summary>
        /// <returns>The SSONet assembly</returns>
        private static Assembly LoadSSONet()
        {
            // get the location of RevitAPI assembly.  SSONet is in the same directory.
            Assembly revitAPIAss = Assembly.GetAssembly(typeof(Autodesk.Revit.DB.XYZ));
            string revitAPIDir = Path.GetDirectoryName(revitAPIAss.Location);
            Debug.Assert(revitAPIDir != null, "revitAPIDir != null");

            //Retrieve the list of referenced assemblies in an array of AssemblyName.
            string strTempAssmbPath = Path.Combine(revitAPIDir, "SSONET.dll");

            //Load the assembly from the specified path. 					
            return Assembly.LoadFrom(strTempAssmbPath);
        }

        /// <summary>
        ///     Callback for registering an authentication provider with the package manager
        /// </summary>
        /// <param name="client">The client, to which the provider will be attached</param>
        internal static void RegisterSingleSignOn()
        {
            singleSignOnAssembly = singleSignOnAssembly ?? LoadSSONet();
        }

    }
}
