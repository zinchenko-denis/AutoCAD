"""Compile/execute native .NET Framework probes without an AutoCAD host.

Windows CI uses the installed .NET SDK and real net48 reference assemblies;
Linux uses Mono. Generated projects and binaries stay in the requested output.
"""
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET


def available():
    return bool(shutil.which('dotnet')) if os.name == 'nt' else all(shutil.which(x) for x in ('mono', 'mcs'))


def command(executable):
    return [str(executable)] if os.name == 'nt' else ['mono', str(executable)]


def compile_probe(sources, references, executable):
    executable = Path(executable).resolve()
    if os.name != 'nt':
        return subprocess.run(['mcs', '-nologo', '-out:' + str(executable),
                               *('-r:' + name + '.dll' for name in references),
                               *(str(Path(p).resolve()) for p in sources)], capture_output=True, text=True)
    project = ET.Element('Project', Sdk='Microsoft.NET.Sdk')
    props = ET.SubElement(project, 'PropertyGroup')
    for name, value in {'OutputType': 'Exe', 'TargetFramework': 'net48', 'LangVersion': 'latest',
                        'EnableDefaultCompileItems': 'false', 'AssemblyName': executable.stem,
                        'GenerateAssemblyInfo': 'false', 'AppendTargetFrameworkToOutputPath': 'false'}.items():
        ET.SubElement(props, name).text = value
    items = ET.SubElement(project, 'ItemGroup')
    for source in sources:
        ET.SubElement(items, 'Compile', Include=str(Path(source).resolve()))
    for reference in references:
        ET.SubElement(items, 'Reference', Include=reference)
    ET.SubElement(items, 'PackageReference', Include='Microsoft.NETFramework.ReferenceAssemblies',
                  Version='1.0.3', PrivateAssets='all')
    filename = executable.with_suffix('.csproj')
    ET.ElementTree(project).write(filename, encoding='utf-8', xml_declaration=True)
    return subprocess.run(['dotnet', 'build', str(filename), '-c', 'Release', '-o', str(executable.parent)],
                          capture_output=True, text=True)
