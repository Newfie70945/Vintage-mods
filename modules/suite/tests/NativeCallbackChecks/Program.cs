using BlockLogger.ServerHealth;
int checks=0;void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
foreach(string type in new[]{"BlockEntityFirepit","BlockEntityBarrel","BlockEntityWildFruitingBush"})Check(NativeBlockCallbackIdentity.IsSurvivalRoot("gmlbVintagestory.GameContent."+type),"official root recognized");
foreach(string path in new[]{"gmlbVintagestory.GameContent.BlockEntityFirepit / Example.Mod.Run","gmlbVintagestory.GameContent.BlockEntityFirepitCustom","gmlbchiseltools.BEDecoBarrel","prefix gmlbVintagestory.GameContent.BlockEntityBarrel",""})Check(!NativeBlockCallbackIdentity.IsSurvivalRoot(path),"child/custom/partial marker stays on normal attribution path");
Console.WriteLine($"PASS {checks} exact native callback identity checks.");
