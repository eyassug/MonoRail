﻿//  Copyright 2004-2011 Castle Project - http://www.castleproject.org/
//  Hamilton Verissimo de Oliveira and individual contributors as indicated. 
//  See the committers.txt/contributors.txt in the distribution for a 
//  full listing of individual contributors.
// 
//  This is free software; you can redistribute it and/or modify it
//  under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 3 of
//  the License, or (at your option) any later version.
// 
//  You should have received a copy of the GNU Lesser General Public
//  License along with this software; if not, write to the Free
//  Software Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA
//  02110-1301 USA, or see the FSF site: http://www.fsf.org.

module Castle.MonoRail.Generator.Api

    open System
    open System.Collections.Generic
    open System.IO
    open System.Reflection
    open System.Web
    open System.CodeDom
    open System.CodeDom.Compiler
    open Castle.MonoRail
    open Castle.MonoRail.Routing
    open Microsoft.CSharp
    open Castle.MonoRail.Hosting.Mvc.Typed
    open System.ComponentModel.Composition
    open System.ComponentModel.Composition.Hosting
    open System.ComponentModel.Composition.Primitives
    open Container

    type ActionDef(controller:Type, action:MethodInfoActionDescriptor, route:Route, index:int) = 
        let _controller = controller
        let _action = action
        let _route = route
        let _index = index

        let get_method (verb:string) : CodeMemberMethod =
            let mmethod = CodeMemberMethod()
            mmethod.Name <- verb
            mmethod.Attributes <- MemberAttributes.Static ||| MemberAttributes.Public
            mmethod.ReturnType <- CodeTypeReference("TargetUrl")

            mmethod

        let append (mmethod: CodeMemberMethod) (includeparams: bool) (fieldAccessor: CodeMethodReturnStatement) (urlType: CodeTypeDeclaration) = 

            // mmethod.Statements.Add fieldAccessor |> ignore

            if includeparams then
                for p in _action.Parameters do
                    let pDecl = CodeParameterDeclarationExpression(p.ParamType, p.Name)
                    //pDecl.CustomAttributes.Add(CodeAttributeDeclaration("Optional")) |> ignore
                    mmethod.Parameters.Add pDecl |> ignore

            // targetTypeDecl.Members.Add field |> ignore
            urlType.Members.Add mmethod |> ignore

        let generate_route_for_verb (verb:string) (urlType: CodeTypeDeclaration) =
            
            let getControllerName (name:string) = 
                if name.EndsWith "Controller" then 
                    name.Substring(0, name.Length - "Controller".Length)
                else 
                    name

            let fieldAccessor = 
                CodeMethodReturnStatement(
                    CodeObjectCreateExpression(
                        CodeTypeReference(typeof<RouteBasedTargetUrl>), 
                        CodePropertyReferenceExpression(PropertyName =  "VirtualPath"),
                        CodeIndexerExpression(
                            CodePropertyReferenceExpression(
                                CodePropertyReferenceExpression(PropertyName = "CurrentRouter"),
                                "Routes"),
                            CodePrimitiveExpression(_route.Name)),
                        CodeObjectCreateExpression(
                            CodeTypeReference(typeof<UrlParameters>),
                            CodePrimitiveExpression(getControllerName(_controller.Name)),
                            CodePrimitiveExpression(_action.Name)
                            )
                        ))
            
            append (get_method verb) false fieldAccessor urlType

            if (verb = "Get") && action.Parameters.Count > 0 then
                append (get_method verb) true fieldAccessor urlType
           
    
        member x.Generate (targetTypeDecl:CodeTypeDeclaration, imports) = 
            (*
                if (_fieldX == null) 
                    _fieldX = new RouteBasedTargetUrl(
                        this.VirtualPath, 
                        this.Current.Routes["default"],
                        new Dictionary<string, string>() { { "controller", "home" }, { "action", "create" } });
                return _fieldX;

            let fieldName = "_field" + (_index.ToString())
            let field = CodeMemberField("TargetUrl", fieldName)
            field.Attributes <- MemberAttributes.Static
            *)

            let urlType = CodeTypeDeclaration(action.Name)
            urlType.BaseTypes.Add(typeof<GeneratedUrlsBase>)
            urlType.TypeAttributes <- TypeAttributes.Abstract ||| TypeAttributes.Public
            targetTypeDecl.Members.Add urlType |> ignore

            generate_route_for_verb "Get" urlType
            generate_route_for_verb "Post" urlType


    let discover_types (binFolder:string) : List<Type> = 
        let blacklisted = [
            "Castle.MonoRail.dll";
            "Castle.MonoRail.Mvc.ViewEngines.Razor.dll";
            "Castle.MonoRail.Routing.dll";
            "System.ComponentModel.Composition.Codeplex.dll";
            "System.Reflection.Context.dll";
            "Microsoft.Web.Infrastructure.dll";
            "System.Web.Helpers.dll";
            "System.Web.Razor.dll";
            "System.Web.WebPages.dll";
            "System.Web.WebPages.Razor.dll"]

        let types = List<Type>()

        for asmfile in Directory.GetFiles(binFolder, "*.dll") do

            let file = Path.GetFileName asmfile
            let found = Seq.exists (fun f -> f = file) blacklisted

            if not found then
                try
                    Console.WriteLine ("Processing " + file)
                    let asm = Assembly.LoadFrom asmfile

                    let loaded_types = 
                        try
                            asm.GetTypes()
                        with
                        | :? ReflectionTypeLoadException as ex -> ex.Types

                    for t in loaded_types do
                        if t != null then
                            types.Add t

                with 
                | ex -> Console.Error.WriteLine ("could not load " + asmfile)

        types

    let init_httpapp (types:List<Type>) =
        let appTypes = 
            types
            |> Seq.filter (fun t -> t.BaseType == typeof<HttpApplication>)

        let flags = BindingFlags.DeclaredOnly|||BindingFlags.Public|||BindingFlags.NonPublic|||BindingFlags.Instance|||BindingFlags.Static

        for app in appTypes do
            let appinstance = Activator.CreateInstance app :?> HttpApplication
            let startMethod = app.GetMethod("Application_Start", flags)
            if startMethod != null then
                // executing for side effects only, so we get the configured routes
                let args = 
                    if startMethod.GetParameters().Length = 2 then
                        [|appinstance;EventArgs.Empty|]:obj[]
                    else
                        [||]
                try
                    if startMethod.IsStatic then
                        startMethod.Invoke(null, args) |> ignore
                    else 
                        startMethod.Invoke(appinstance, args) |> ignore
                with 
                | ex -> 
                    Console.Error.WriteLine ("could not run Application_Start for " + app.FullName)
                    Console.Error.WriteLine "Routes may have not been evaluated"
                    Console.Error.WriteLine (ex.ToString())

        if Seq.isEmpty Router.Instance.Routes then
            Console.Error.WriteLine "No routes found"
            Environment.Exit -5

        Console.WriteLine ""
        Console.WriteLine "Routes discovered"

        for r in Router.Instance.Routes do
            Console.WriteLine ("\t" + r.Name + "  " + r.Path)

    let best_route_for controllerName (action:ControllerActionDescriptor) = 
            try
                // this is likely to get very complex, especially when areas support is added
                let best_route = 
                    Router.Instance.Routes
                    |> Seq.find (fun r -> r.Path.StartsWith("/" + controllerName)) 
                best_route
            with
            | ex -> 
                let def = 
                    Router.Instance.Routes
                    |> Seq.find (fun r -> r.Name = "default") 
                def

    let generate_routes (binFolder:string) (targetFolder:string) = 
        
        Console.WriteLine ("Bin folder: " + binFolder)
        Console.WriteLine ("Target folder: " + targetFolder)

        let types = discover_types binFolder

        init_httpapp types
        
        let controllers = 
            types 
            |> Seq.filter (fun t -> t.Name.EndsWith("Controller") || typeof<IViewComponent>.IsAssignableFrom(t))
            |> Seq.map (fun t -> (t, t.Name.Substring(0, t.Name.Length - "Controller".Length)))

        let controller2route = Dictionary<Type, List<ActionDef>>()

        Console.WriteLine ""

        let opts = CompositionOptions.IsThreadSafe ||| CompositionOptions.DisableSilentRejection

        let tempContainer = new CompositionContainer(new BasicComposablePartCatalog([|AggregatePartDefinition(binFolder)|]), opts)

        let descBuilder = tempContainer.GetExportedValue<ControllerDescriptorBuilder>()

        for ct,name in controllers do
            Console.WriteLine ("Processing " + ct.FullName)

            let desc = descBuilder.Build ct
            
            let defs = List<ActionDef>()
            controller2route.[ct] <- defs

            let mutable index = 1;

            for action in desc.Actions do    
                Console.WriteLine ("    Action: " + action.Name)
                let def = ActionDef(ct, action :?> MethodInfoActionDescriptor, (best_route_for name action), index)
                index <- index + 1
                defs.Add def

        let compilationUnit = CodeCompileUnit()
        let imports = HashSet<string>()
        imports.Add "System" |> ignore
        imports.Add "System.Web" |> ignore
        imports.Add "Castle.MonoRail" |> ignore
        imports.Add "Castle.MonoRail.Routing" |> ignore
        imports.Add "System.Runtime.InteropServices" |> ignore

        for pair in controller2route do
            let ct = pair.Key
            let defs = pair.Value
            let ns = 
                let found = 
                    compilationUnit.Namespaces 
                    |> Seq.cast 
                    |> Seq.filter (fun (ns:CodeNamespace) -> ns.Name = ct.Namespace)

                if Seq.isEmpty found then 
                    let newns = CodeNamespace(ct.Namespace)
                    compilationUnit.Namespaces.Add newns |> ignore
                    newns
                else 
                    Seq.head found
    
            let typeDecl = CodeTypeDeclaration(ct.Name)
            typeDecl.TypeAttributes <- TypeAttributes.Public
            typeDecl.IsPartial <- true
            ns.Types.Add typeDecl |> ignore

            let staticType = CodeTypeDeclaration("Urls")
            staticType.TypeAttributes <- TypeAttributes.Abstract ||| TypeAttributes.Public
            typeDecl.Members.Add staticType |> ignore
    
            for def in defs do
                def.Generate(staticType, imports)
      
        for ns in compilationUnit.Namespaces do
            for import in imports do
                ns.Imports.Add (CodeNamespaceImport(import)) |> ignore

        let csprovider = new CSharpCodeProvider()

        //csprovider.GenerateCodeFromCompileUnit(compilationUnit, Console.Out, CodeGeneratorOptions())
        if targetFolder <> null then
            use stream = File.CreateText(Path.Combine(targetFolder, "GeneratedRoutes.cs"))
            csprovider.GenerateCodeFromCompileUnit(compilationUnit, stream, CodeGeneratorOptions())
        else
            csprovider.GenerateCodeFromCompileUnit(compilationUnit, Console.Out, CodeGeneratorOptions())