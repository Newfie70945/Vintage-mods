const fs=require('node:fs'),path=require('node:path');
module.exports=function embeddedPage(){
 const source=fs.readFileSync(path.join(__dirname,'../../src/WebDashboard/WebDashboardPage.cs'),'utf8');
 const release=fs.readFileSync(path.join(__dirname,'../../src/SuiteRelease.cs'),'utf8');
 const motd=fs.readFileSync(path.join(__dirname,'../../src/WebDashboard/WebDashboardMotdScript.cs'),'utf8');
 const patches=fs.readFileSync(path.join(__dirname,'../../src/WebDashboard/WebDashboardPatchGeneratorScript.cs'),'utf8').split('public const string Script = """')[1].split('""";')[0];
 const declarations=motd.split('public const string Declarations = """')[1].split('""";')[0],bindings=motd.split('public const string Bindings = """')[1].split('""";')[0];
 const roles=fs.readFileSync(path.join(__dirname,'../../src/WebDashboard/WebDashboardRoles.cs'),'utf8');
 const roleHtml=roles.split('public const string Html = """')[1].split('""";')[0],roleScript=roles.split('public const string Script = """')[1].split('""";')[0];
 const commands=fs.readFileSync(path.join(__dirname,'../../src/WebDashboard/WebDashboardPlayerCommandsScript.cs'),'utf8').split('public const string Script = """')[1].split('""";')[0];
 const polish=fs.readFileSync(path.join(__dirname,'../../src/WebDashboard/WebDashboardPolishScript.cs'),'utf8');
 const part=name=>polish.split('public const string '+name+' = \"\"\"')[1].split('\"\"\";')[0];
 const style=fs.readFileSync(path.join(__dirname,'../../src/WebDashboard/WebDashboardInterfaceStyle.cs'),'utf8');
 const interfacePart=name=>style.split('public const string '+name+' = \"\"\"')[1].split('\"\"\";')[0];
 const version=release.match(/Version = "([^"]+)"/)[1],suffix=release.match(/BuildSuffix = "([^"]+)"/)[1];
 return source.split('public static readonly string Html = """')[1].split('""".Replace')[0].replaceAll('__NSS_VERSION__',version).replaceAll('__NSS_BUILD_ID__',`newfies-server-essentials-suite-${version}-${suffix}`).replace('__NSS_MOTD_DECLARATIONS__',declarations).replace('__NSS_MOTD_BINDINGS__',bindings).replace('__NSS_PATCH_GENERATOR_SCRIPT__',patches).replace('__NSS_ROLE_HTML__',roleHtml).replace('__NSS_ROLE_SCRIPT__',roleScript).replace('__NSS_PLAYER_COMMANDS_SCRIPT__',commands).replace('__NSS_POLISH_SCRIPT__',part('Script')).replace('__NSS_INTERFACE_STYLE__',interfacePart('Style')).replace('__NSS_INTERFACE_SCRIPT__',interfacePart('Script')).replace('__NSS_RETENTION_HTML__',part('RetentionHtml')).replace('__NSS_INVESTIGATION_HTML__',part('InvestigationHtml'));
};
