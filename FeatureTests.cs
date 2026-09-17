using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public static class FeatureTests {
 internal static void Check(bool condition,string label){if(!condition)throw new Exception(label);}
 static SequenceItem Item(string name,int runs){return new SequenceItem{Name=name,Runs=runs,Template=new Template{Repeats=77,Gap=17,Steps=new List<Step>{new Step{Type="鍵盤按壓",Value=name,Hold=0,Delay=23}}}};}
 public static void Run(){TestColorScan();
  TestTemplateSaving();
  TestBatchAndSeconds();
  TestLiveUI();
  TestInteraction();
  var plan=new SequencePlan{TransitionDelay=31,Items=new List<SequenceItem>{Item("A",1000),Item("B",1000),Item("C",500)}};
  var trace=new List<string>();var delays=new List<int>();Func<int,CancellationToken,Task> wait=(ms,ct)=>{ct.ThrowIfCancellationRequested();delays.Add(ms);return Task.FromResult(0);};
  MacroRunner.RunSequence(plan,CancellationToken.None,s=>{},(step,ct)=>{trace.Add(step.Value);return Task.FromResult(0);},wait).GetAwaiter().GetResult();
  Check(trace.SequenceEqual(Enumerable.Repeat("A",1000).Concat(Enumerable.Repeat("B",1000)).Concat(Enumerable.Repeat("C",500))),"A1000 B1000 C500 execution order and override");Check(delays.Count(ms=>ms==31)==2&&delays.Count(ms=>ms==17)==2497&&delays.Count(ms=>ms==23)==2500,"Sequence timing");
  using(var stop=new CancellationTokenSource()){trace.Clear();bool canceled=false;try{MacroRunner.RunSequence(plan,stop.Token,s=>{},(step,ct)=>{trace.Add(step.Value);stop.Cancel();return Task.FromResult(0);},wait).GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}Check(canceled&&trace.Count==1,"Cancel halts remaining templates");}
  using(var stop=new CancellationTokenSource()){trace.Clear();bool canceled=false;var shortPlan=new SequencePlan{TransitionDelay=31,Items=new List<SequenceItem>{Item("A",1),Item("B",1)}};try{MacroRunner.RunSequence(shortPlan,stop.Token,s=>{},(step,ct)=>{trace.Add(step.Value);return Task.FromResult(0);},(ms,ct)=>{if(ms==31)stop.Cancel();ct.ThrowIfCancellationRequested();return Task.FromResult(0);}).GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}Check(canceled&&trace.SequenceEqual(new[]{"A"}),"Cancel during transition");}
  trace.Clear();bool failed=false;try{MacroRunner.RunSequence(plan,CancellationToken.None,s=>{},(step,ct)=>{trace.Add(step.Value);throw new Exception("simulated failure");},wait).GetAwaiter().GetResult();}catch{failed=true;}Check(failed&&trace.Count==1,"Action failure aborts sequence");
  var copy=Json.Copy(plan);SequencePlan.Validate(copy);plan.Items[0].Template.Steps[0].Value="Z";Check(copy.Items[0].Template.Steps[0].Value=="A"&&copy.Items[2].Runs==500,"Embedded templates roundtrip");copy.Items[0].Runs=0;bool bad=false;try{SequencePlan.Validate(copy);}catch{bad=true;}Check(bad,"No infinite stage counts");
  Check(KeyPicker.FromKeyData(Keys.A)=="A"&&KeyPicker.FromKeyData(Keys.Control|Keys.Shift|Keys.S)=="Ctrl+Shift+S"&&KeyPicker.FromKeyData(Keys.D1)=="1","Key capture mapping");
  using(var picker=new KeyPicker()){Check(picker.Value=="Space","Single-key default");Check(picker.Controls.OfType<Button>().Count()==1&&!picker.Controls.OfType<Label>().Any(),"Only capture button and default key remain");picker.Value="Ctrl+C";
   var capture=picker.Controls.OfType<Button>().First(b=>b.Text=="捕捉按鍵");var input=picker.Controls.OfType<TextBox>().First();var down=input.GetType().GetMethod("OnKeyDown",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
   capture.PerformClick();down.Invoke(input,new object[]{new KeyEventArgs(Keys.A)});Check(picker.Value=="A"&&!picker.Recording,"Capture actual single-key event");capture.PerformClick();down.Invoke(input,new object[]{new KeyEventArgs(Keys.Control|Keys.S)});Check(picker.Value=="Ctrl+S"&&!picker.Recording,"Capture actual combination event");capture.PerformClick();down.Invoke(input,new object[]{new KeyEventArgs(Keys.Escape)});Check(picker.Value=="Ctrl+S"&&!picker.Recording&&!KeyPicker.AnyRecording,"Capture cancel preserves value");
  }
  using(var main=new MainForm()){var nums=Descendants(main).OfType<NumericUpDown>();Check(nums.Count(n=>n.Maximum==1000000&&n.Value==0)==1&&nums.Count(n=>n.Maximum==86400&&n.Value==1)==2,"1000ms default delay and gap");Check(Descendants(main).OfType<CrosshairDrag>().Count()==1,"New mouse action has draggable crosshair");}
  using(var form=new SequenceForm(()=>true)){foreach(var item in plan.Items)form.AddItem(item);var panel=form.Controls[0];form.Controls.Remove(panel);panel.Size=form.ClientSize;panel.CreateControl();panel.PerformLayout();using(var bitmap=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-sequence.png"));}panel.Dispose();}
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"feature-test-result.txt"),"PASS: exact A1000/B1000/C500 order; repeat override; action, cycle and transition intervals; cancellation within action and transition; failure abort; embedded snapshot roundtrip; positive repeat validation; single-key defaults; capture mapping; common-key replacement; 1000ms defaults; new crosshair control. Input execution mocked; no desktop input sent.");
 }
 static void TestTemplateSaving(){
  var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
  Func<MainForm,string,object[],object> invoke=(form,name,args)=>typeof(MainForm).GetMethod(name,flags).Invoke(form,args);
  Func<MainForm,bool> dirty=form=>(bool)invoke(form,"HasUnsavedChanges",new object[0]);
  string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"save-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string path=Path.Combine(folder,"original.json");
  using(var form=new MainForm()){
   Check(!dirty(form),"New empty template is clean");var a=new Step{Type="滑鼠點擊",Value="左鍵",X=12,Y=34,Hold=50,Delay=500,Notes="original"};invoke(form,"AddStep",new object[]{a});Check(dirty(form),"New action is unsaved");
   invoke(form,"SaveToPath",new object[]{path});Check(File.Exists(path)&&!dirty(form),"Save establishes baseline");a.Notes="changed";Check(dirty(form),"Notes change detected");
   object[] hotkey={new Message(),Keys.Control|Keys.S};Check((bool)invoke(form,"ProcessCmdKey",hotkey),"Ctrl+S handled");var saved=Json.Read<Template>(File.ReadAllText(path));Check(saved.Steps[0].Notes=="changed"&&!dirty(form),"Ctrl+S overwrites original and clears dirty state");
   a.Notes="temporary";a.Notes="changed";Check(!dirty(form),"Reverting changes restores clean state");a.Delay=1000;bool failed=false;try{invoke(form,"SaveToPath",new object[]{Path.Combine(folder,"missing","bad.json")});}catch(System.Reflection.TargetInvocationException){failed=true;}Check(failed&&dirty(form),"Failed save preserves unsaved state");
   invoke(form,"SaveCurrentTemplate",new object[0]);Check(!dirty(form)&&Json.Read<Template>(File.ReadAllText(path)).Steps[0].Delay==1000,"Failed save does not change target path");
   var grid=Descendants(form).OfType<ActionGrid>().Single();grid.Rows.Clear();Check(dirty(form),"Deleting all actions remains unsaved");
  }
  using(var loaded=new MainForm()){
   var template=Json.Read<Template>(File.ReadAllText(path));foreach(var step in template.Steps)invoke(loaded,"AddStep",new object[]{step});invoke(loaded,"MarkSaved",new object[]{path});Check(!dirty(loaded)&&(bool)invoke(loaded,"ConfirmLeave",new object[0]),"Loaded template can leave without warning");template.Steps[0].X=999;Check(dirty(loaded),"Loaded template edits detected");invoke(loaded,"SaveCurrentTemplate",new object[0]);Check(Json.Read<Template>(File.ReadAllText(path)).Steps[0].X==999,"Loaded template saves to original path");
  }
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"save-test-result.txt"),"PASS: clean/new/loaded states, Ctrl+S original path overwrite, notes and deletion detection, revert-to-clean, failed save retains dirty state and target, loaded template save. No desktop input sent.");
 }
 static void TestBatchAndSeconds(){
  Check(WaitUnits.ToMilliseconds(1)==1000&&WaitUnits.ToMilliseconds(.5m)==500&&WaitUnits.ToMilliseconds(.001m)==1&&WaitUnits.ToMilliseconds(0)==0,"Seconds conversion exact");
  foreach(decimal n in new[]{-.1m,.0001m,86400.001m}){bool rejected=false;try{WaitUnits.ToMilliseconds(n);}catch{rejected=true;}Check(rejected,"Seconds range and precision");}
  var a=new Step{Type="滑鼠點擊",Value="左鍵",X=10,Y=20,Hold=50,Delay=500,Notes="A"};var b=new Step{Type="滑鼠點擊",Value="右鍵",X=30,Y=40,Hold=80,Delay=1250,Notes="B"};
  var changed=new BatchPatch{Delay=2000}.Apply(new[]{a,b});Check(changed.All(s=>s.Delay==2000)&&changed[0].X==10&&changed[1].X==30&&changed[1].Hold==80&&changed[1].Value=="右鍵"&&changed[1].Notes=="B"&&b.Delay==1250,"Batch only selected fields change");
  changed=new BatchPatch{SetNotes=true,Notes=""}.Apply(new[]{a,b});Check(changed.All(s=>s.Notes=="")&&a.Notes=="A","Batch clear notes without source mutation");
  bool mixed=false;try{new BatchPatch{Delay=1000}.Apply(new[]{a,new Step{Type="等待",Delay=1000}});}catch{mixed=true;}Check(mixed,"Mixed types rejected");bool bad=false;try{new BatchPatch{Hold=-1}.Apply(new[]{a,b});}catch{bad=true;}Check(bad&&a.Hold==50&&b.Hold==80,"Invalid batch atomicity");
  Check(WaitUnits.RowColor("滑鼠點擊")!=WaitUnits.RowColor("鍵盤按壓")&&WaitUnits.RowColor("鍵盤按壓")!=WaitUnits.RowColor("等待"),"Distinct row colors");
  using(var editor=new StepEditor(a)){Check(editor.BuildResult().Delay==500,"Legacy milliseconds roundtrip in seconds editor");}
  using(var dialog=new BatchEditor(new[]{a,b})){var panel=dialog.Controls[0];dialog.Controls.Remove(panel);panel.Size=dialog.ClientSize;panel.CreateControl();using(var bitmap=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-batch.png"));}panel.Dispose();}
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"batch-test-result.txt"),"PASS: seconds conversion and precision, old milliseconds roundtrip, selective batch patch, note clearing, mixed-type rejection, invalid batch atomicity, distinct row colors. No desktop input sent.");
 }
 static void TestLiveUI(){
  var estimatePlan=new SequencePlan{TransitionDelay=500,Items=new List<SequenceItem>{
   new SequenceItem{Name="A",Runs=2,Template=new Template{Gap=100,Steps=new List<Step>{new Step{Type="鍵盤按壓",Value="A",Hold=50,Delay=200},new Step{Type="等待",Hold=999,Delay=300}}}},
   new SequenceItem{Name="B",Runs=1,Template=new Template{Gap=999,Steps=new List<Step>{new Step{Type="等待",Delay=250}}}}
  }};
  Check(SequenceForm.EstimateMilliseconds(estimatePlan)==4953&&SequenceForm.FormatDuration(4953)=="5 秒","Estimate holds, waits, cycle gaps, transitions and countdown");
  Check(SequenceForm.EstimateMilliseconds(new SequencePlan())==0,"Empty sequence estimate");
  estimatePlan.Items[0].TransitionDelay=1750;estimatePlan.Items[1].TransitionDelay=9000;
  Check(SequenceForm.EstimateMilliseconds(estimatePlan)==6203,"Per-template transition estimate excludes final transition");
  var transitionWaits=new List<int>();MacroRunner.RunSequence(estimatePlan,CancellationToken.None,s=>{},(a,ct)=>Task.FromResult(0),(ms,ct)=>{transitionWaits.Add(ms);return Task.FromResult(0);}).GetAwaiter().GetResult();
  Check(transitionWaits.Contains(1750)&&!transitionWaits.Contains(9000),"Runner uses per-template transition");
  var persisted=Json.Copy(estimatePlan);Check(persisted.Items[0].TransitionDelay==1750,"Per-template transition persists");
  var repeated=new SequencePlan{OuterRuns=2,OuterGap=700,Items=new List<SequenceItem>{Item("A",1),Item("B",1)},TransitionDelay=300};
  var repeatedTrace=new List<string>();var repeatedWaits=new List<int>();
  MacroRunner.RunSequence(repeated,CancellationToken.None,s=>{},(a,ct)=>{repeatedTrace.Add(a.Value);return Task.FromResult(0);},(ms,ct)=>{repeatedWaits.Add(ms);return Task.FromResult(0);}).GetAwaiter().GetResult();
  Check(string.Join(",",repeatedTrace)=="A,B,A,B"&&repeatedWaits.Count(ms=>ms==700)==1&&repeatedWaits.Count(ms=>ms==300)==2,"Whole sequence loops in order with outer wait only between cycles");
  Check(SequenceForm.EstimateMilliseconds(repeated)==4396,"Repeated sequence estimate counts initial countdown once");
  repeated.OuterRuns=0;Check(SequenceForm.EstimateMilliseconds(repeated)==-1,"Continuous sequence estimate");
  using(var stop=new CancellationTokenSource()){int actions=0;bool stopped=false;try{MacroRunner.RunSequence(repeated,stop.Token,s=>{},(a,ct)=>{if(++actions==3)stop.Cancel();return Task.FromResult(0);},(ms,ct)=>{ct.ThrowIfCancellationRequested();return Task.FromResult(0);}).GetAwaiter().GetResult();}catch(OperationCanceledException){stopped=true;}Check(stopped&&actions==3,"Continuous whole sequence cancellation");}
  var legacyPlan=Json.Read<SequencePlan>("{\"Kind\":\"MacroSequence\",\"Version\":1,\"Items\":[]}");
  Check(legacyPlan.OuterRuns==1&&legacyPlan.OuterGap==1000,"Old sequence defaults to one pass");
  using(var iconStream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("EvaMacroStudio.AppIcon")){Check(iconStream!=null&&AppIdentity.Icon.Width>0,"Embedded application icon");}
  var original=new Step{Type="滑鼠點擊",Value="左鍵",X=20,Y=30,Hold=50,Delay=1000,Notes="before"};Check(CellEdits.Apply(original,6,"1.5").Delay==1500&&original.Delay==1000,"Cell edit clone isolation");Check(CellEdits.Apply(original,2,"-200").X==-200&&CellEdits.Apply(original,7,"測試 123").Notes=="測試 123","Cell coordinates and notes");foreach(string invalid in new[]{"-1","1.0001","abc","999999999999"}){bool rejected=false;try{CellEdits.Apply(original,6,invalid);}catch{rejected=true;}Check(rejected,"Reject invalid cell numeric input");}Check(!CellEdits.CanEdit(original,0)&&!CellEdits.CanEdit(new Step{Type="等待"},2),"Nonapplicable cells read only");
  var forecasts=new List<string>();var forecastTemplate=new Template{Gap=40,Steps=new List<Step>{new Step{Type="鍵盤按壓",Value="A",Hold=20,Delay=100},new Step{Type="等待",Delay=200}}};
  MacroRunner.RunTemplate(forecastTemplate,2,CancellationToken.None,s=>{},(a,ct)=>Task.FromResult(0),(ms,ct)=>Task.FromResult(0),upcoming:(a,ms)=>forecasts.Add((a==null?"完成":a.Type)+":"+ms)).GetAwaiter().GetResult();
  Check(forecasts.Take(4).SequenceEqual(new[]{"等待:120","等待:100","鍵盤按壓:241","鍵盤按壓:241"})&&forecasts.Last().StartsWith("完成:"),"Upcoming action includes hold, delay and cycle gap");
  forecasts.Clear();MacroRunner.RunSequence(new SequencePlan{TransitionDelay=70,Items=new List<SequenceItem>{Item("A",1),Item("B",1)}},CancellationToken.None,s=>{},(a,ct)=>Task.FromResult(0),(ms,ct)=>Task.FromResult(0),upcoming:(a,ms)=>forecasts.Add((a==null?"完成":a.Value)+":"+ms)).GetAwaiter().GetResult();
  Check(forecasts.Contains("B:94")&&forecasts.Contains("B:70")&&forecasts.Last().StartsWith("完成:"),"Upcoming action crosses template boundary");
  Check(RunBadge.DescribeUpcoming(new Step{Type="等待"},1250).Contains("1.3 sec")&&RunBadge.DescribeUpcoming(null,0).Contains("即將完成"),"Countdown formatting and final action");
  var remaining=new List<string>();var plan=new SequencePlan{Items=new List<SequenceItem>{Item("A",2),Item("B",1)}};MacroRunner.RunSequence(plan,CancellationToken.None,s=>{},(step,ct)=>Task.FromResult(0),(ms,ct)=>Task.FromResult(0),(name,n)=>remaining.Add(name+":"+n)).GetAwaiter().GetResult();Check(remaining.SequenceEqual(new[]{"A:2","A:1","A:1","A:0","B:1","B:0"}),"Template name and remaining cycles");Check(RunBadge.Describe("A",-1).Contains("持續循環"),"Unlimited progress display");
  using(var main=new MainForm()){var add=typeof(MainForm).GetMethod("AddStep",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);add.Invoke(main,new object[]{original});var grid=Descendants(main).OfType<ActionGrid>().Single();var panel=main.Controls[0];main.Controls.Remove(panel);panel.Size=main.ClientSize;panel.CreateControl();grid.CurrentCell=grid.Rows[0].Cells[6];Check(grid.BeginEdit(true),"Begin inline edit");((TextBox)grid.EditingControl).Text="2.3";Check(grid.EndEdit()&&((Step)grid.Rows[0].Tag).Delay==2300,"Inline UI edit commits to model");grid.CurrentCell=grid.Rows[0].Cells[6];grid.BeginEdit(true);((TextBox)grid.EditingControl).Text="-5";bool ended=grid.EndEdit();Check(((Step)grid.Rows[0].Tag).Delay==2300,"Invalid inline edit leaves saved value unchanged");grid.CancelEdit();panel.Dispose();}
  using(var sequence=new SequenceForm(()=>true)){
   sequence.AddItem(Item("initial",2));var panel=sequence.Controls[0];sequence.Controls.Remove(panel);panel.Size=sequence.ClientSize;panel.CreateControl();
   var grid=Descendants(panel).OfType<DataGridView>().Single();var doubleClick=typeof(ActionGrid).GetMethod("OnMouseDoubleClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
   int[] columns={1,2,4,5};string[] values={"renamed","17","1.25","2.5"};
   for(int i=0;i<columns.Length;i++){var rect=grid.GetCellDisplayRectangle(columns[i],0,false);doubleClick.Invoke(grid,new object[]{new MouseEventArgs(MouseButtons.Left,2,rect.Left+5,rect.Top+5,0)});Application.DoEvents();Check(grid.IsCurrentCellInEditMode,"Sequence double click starts column "+columns[i]);((TextBox)grid.EditingControl).Text=values[i];Check(grid.EndEdit(),"Sequence cell commits "+columns[i]);}
   var item=sequence.Current().Items[0];Check(item.Name=="renamed"&&item.Runs==17&&item.Template.Gap==1250&&item.TransitionDelay==2500,"All editable sequence cells update model");panel.Dispose();
  }
  using(var sequence=new SequenceForm(()=>true)){
   foreach(string name in new[]{"A","B","C","D"})sequence.AddItem(Item(name,2));
   var grid=Descendants(sequence).OfType<ActionGrid>().Single();
   Action<string,object[]> invoke=(name,args)=>typeof(SequenceForm).GetMethod(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(sequence,args);
   grid.ClearSelection();grid.Rows[0].Selected=true;grid.Rows[2].Selected=true;invoke("CopyItems",new object[0]);invoke("PasteItems",new object[0]);
   Check(string.Join(",",sequence.Current().Items.Select(i=>i.Name))=="A,B,C,A,C,D"&&grid.SelectedRows.Count==2,"Sequence multiple paste order");
   sequence.Current().Items[3].Template.Steps[0].Notes="copy";Check(sequence.Current().Items[0].Template.Steps[0].Notes!="copy","Sequence clipboard deep copy");
   invoke("DeleteItem",new object[0]);Check(string.Join(",",sequence.Current().Items.Select(i=>i.Name))=="A,B,C,D","Sequence multi delete");
   invoke("ReorderItems",new object[]{new[]{0,2},4});Check(string.Join(",",sequence.Current().Items.Select(i=>i.Name))=="B,D,A,C"&&grid.SelectedRows.Count==2,"Sequence multi reorder preserves selection");

   var cycleGap=(NumericUpDown)typeof(SequenceForm).GetField("cycleGap",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(sequence);cycleGap.Value=1.5m;
   Check(sequence.Current().Items.Skip(2).All(i=>i.Template.Gap==1500&&i.Runs==2)&&sequence.Current().Items[0].Template.Gap==17,"Selective batch edit only selected templates");
   grid.SelectAll();invoke("DeleteItem",new object[0]);Check(sequence.Current().Items.Count==0,"Delete entire sequence");
  }
  using(var badge=new RunBadge())using(var bitmap=new Bitmap(badge.Width,badge.Height)){badge.SetProgress("A 範本",999);badge.SetUpcoming(new Step{Type="鍵盤按壓"},3250);badge.DrawToBitmap(bitmap,new Rectangle(Point.Empty,badge.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-running.png"));}
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"live-ui-test-result.txt"),"PASS: inline numeric UI commit, invalid numeric rejection without mutation, coordinate/notes cell edits, protected cells, sequence name/remaining progress, continuous-loop label. No desktop input sent.");
 }
 static void TestInteraction(){
  var list=new[]{"A","B","C","D","E"};
  Check(ActionGrid.Reorder(list,new[]{1},5).SequenceEqual(new[]{"A","C","D","E","B"}),"Drag single down");
  Check(ActionGrid.Reorder(list,new[]{3},0).SequenceEqual(new[]{"D","A","B","C","E"}),"Drag single up");
  Check(ActionGrid.Reorder(list,new[]{2,1},5).SequenceEqual(new[]{"A","D","E","B","C"}),"Multi-drag maintains original order");
  Check(ActionGrid.Reorder(list,new[]{2,3},0).SequenceEqual(new[]{"C","D","A","B","E"}),"Multi-drag up");
  Check(ActionGrid.Reorder(list,new[]{1,2},2).SequenceEqual(list),"Drop within selected block is unchanged");
  Check(ActionGrid.Reorder(list,new[]{0,2,4},5).SequenceEqual(new[]{"B","D","A","C","E"}),"Noncontiguous selection");
  var shown=new List<int>();var waits=new List<int>();CountdownOverlay.Count(CancellationToken.None,n=>shown.Add(n),(ms,ct)=>{waits.Add(ms);return Task.FromResult(0);}).GetAwaiter().GetResult();Check(shown.SequenceEqual(new[]{3,2,1})&&waits.SequenceEqual(new[]{1000,1000,1000}),"321 countdown timing");
  using(var stop=new CancellationTokenSource()){shown.Clear();bool canceled=false;try{CountdownOverlay.Count(stop.Token,n=>{shown.Add(n);if(n==2)stop.Cancel();},(ms,ct)=>{ct.ThrowIfCancellationRequested();return Task.FromResult(0);}).GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}Check(canceled&&shown.SequenceEqual(new[]{3,2}),"Countdown cancellation");}
  string note="測試 123 !@#\r\n第二行 😀 \"quoted\"";var step=new Step{Type="滑鼠點擊",Value="左鍵",X=12,Y=34,Delay=1000,Notes=note};Check(Json.Copy(step).Notes==note&&MainForm.CopyStep(step).Notes==note,"Notes copied and persisted exactly");
  using(var editor=new StepEditor(step)){Check(editor.BuildResult().Notes==note,"Notes loaded for editing");Descendants(editor).OfType<TextBox>().First(t=>t.Multiline).Text="修改\n456";Check(editor.BuildResult().Notes=="修改\n456"&&step.Notes==note,"Edit notes without mutating original");var panel=editor.Controls[0];editor.Controls.Remove(panel);panel.Size=editor.ClientSize;panel.CreateControl();using(var bitmap=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-notes-edit.png"));}panel.Dispose();}
  using(var main=new MainForm()){
   var grid=Descendants(main).OfType<ActionGrid>().Single();var add=typeof(MainForm).GetMethod("AddStep",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);for(int i=0;i<5;i++)add.Invoke(main,new object[]{new Step{Type="滑鼠點擊",Value="左鍵",X=100+i,Y=200,Delay=1000,Notes="備註 "+i}});
   var reorder=typeof(MainForm).GetMethod("ReorderActions",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);reorder.Invoke(main,new object[]{new[]{1,2},5});var steps=grid.Rows.Cast<DataGridViewRow>().Select(r=>(Step)r.Tag).ToList();Check(steps.Select(s=>s.X).SequenceEqual(new[]{100,103,104,101,102})&&steps.Select(s=>s.Sequence).SequenceEqual(new[]{1,2,3,4,5}),"UI reorder renumbers");Check(grid.SelectedRows.Count==2&&steps[3].Notes=="備註 1"&&MainForm.BuildMarkers(steps)[3].Label=="4","UI reorder preserves selection, notes and marker numbering");
  }
  using(var overlay=new CountdownOverlay())using(var bitmap=new Bitmap(overlay.Width,overlay.Height)){overlay.DrawToBitmap(bitmap,new Rectangle(Point.Empty,overlay.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-countdown.png"));}
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"interaction-test-result.txt"),"PASS: single/multiple/noncontiguous reorder, no-op drop, original relative order, grid selection retention, marker renumbering, multiline Unicode notes persistence/copy/edit isolation, 3-2-1 countdown timing and cancellation. No desktop input was sent.");
 }
 static IEnumerable<Control> Descendants(Control c){foreach(Control child in c.Controls){yield return child;foreach(var d in Descendants(child))yield return d;}}

 // 顏色掃描小工具的測試。一律不顯示視窗、不註冊熱鍵、不動使用者的設定檔，
 // 只在執行檔旁產生預覽圖與測試資料。
 static int[] Canvas(int width, int height, Color background, Rectangle patch, Color fill)
 {
  var pixels = new int[width * height]; int back = background.ToArgb(), front = fill.ToArgb();
  for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) pixels[y * width + x] = patch.Contains(x, y) ? front : back;
  return pixels;
 }
 // 測試裡最常要的就是「一個色碼、一個誤差」的規則，包成一行。
 static List<ColorMatch> One(Color target, int tolerance)
 {
  return new List<ColorMatch> { new ColorMatch(target, tolerance) };
 }
 static void TestColorScan()
 {
  Check(ColorRule.Parse("#FF0000") == Color.FromArgb(255, 255, 0, 0), "Hex color parsing");
  Check(ColorRule.Parse("ff0000") == ColorRule.Parse("#f00"), "Short hex and missing hash");
  Check(ColorRule.Format(ColorRule.Parse("Lime")) == "#00FF00", "Named color parsing and formatting");
  foreach (string bad in new[] { "", "#12", "xyzxyz", "#GGGGGG" }) { bool rejected = false; try { ColorRule.Parse(bad); } catch { rejected = true; } Check(rejected, "Invalid color accepted: " + bad); }
  Check(ColorScanner.Difference(Color.FromArgb(255, 100, 100, 100).ToArgb(), Color.FromArgb(120, 100, 100)) == 20, "Channel difference uses the largest gap");

  // ── Lab 與 ΔE ──
  // 這幾條是整個比對的地基，所以先釘住轉換本身，不然上面所有的距離都沒有意義。
  {
   float L, a, b;
   Perceptual.ToLab(Color.White, out L, out a, out b);
   Check(L > 99.5f && L < 100.5f && Math.Abs(a) < 1f && Math.Abs(b) < 1f, "White sits at L=100 with no colour cast");
   Perceptual.ToLab(Color.Black, out L, out a, out b);
   Check(Math.Abs(L) < .5f && Math.Abs(a) < 1f && Math.Abs(b) < 1f, "Black sits at the origin");
   Check(Perceptual.Distance(Color.Black, Color.White) > 99f, "Black to white spans the whole lightness axis");
   // 官方 CIEDE2000 測試向量（Sharma et al.）。公式抄錯的話這一條會立刻抓到。
   Check(Math.Abs(Perceptual.Distance(50f, 2.5f, 0f, 50f, 0f, -2.5f) - 4.3065f) < .001f, "CIEDE2000 matches the published reference value");
   Check(Math.Abs(Perceptual.Distance(50f, 2.5f, 0f, 73f, 25f, -18f) - 27.1492f) < .001f, "And it matches the reference for a large difference too");
   Check(Perceptual.Distance(Color.Red, Color.Red) < .01f, "A colour is zero distance from itself");
   // Lab → sRGB 再轉回來必須回到原處，否則容差預覽畫出來的極限色會系統性偏掉。
   foreach (var sample in new[] { Color.Red, Color.Lime, Color.Blue, Color.FromArgb(46, 48, 69), Color.FromArgb(0, 42, 75), Color.FromArgb(151, 150, 157) })
   {
    Perceptual.ToLab(sample, out L, out a, out b);
    Check(Perceptual.Distance(Perceptual.FromLab(L, a, b), sample) < 1f, "Lab roundtrip returns the same colour: " + ColorRule.Format(sample));
   }
   // 色域外的座標要夾回 sRGB 而不是回傳垃圾值。
   var clamped = Perceptual.FromLab(50f, 200f, -200f);
   Check(clamped.A == 255, "An out-of-gamut Lab coordinate still produces a usable colour");
  }

  // ── 實際從遊戲畫面吸到的資料 ──
  // 這是整個改用 Lab 的理由，所以原始資料直接釘成測試。
  //
  // 關鍵事實：色相分不開這些材質。布的畫面取樣在 280.5~286.3 度，皮革在 285.6~287.6 度，
  // 兩段是重疊的——用色相衡量根本切不開。而且這些材質都是暗色低彩度（金屬彩度只有 2.8~4.5），
  // 那個區域的色相既不穩定也不影響觀感，拿它當主要指標就是放大了一個不重要的維度。
  {
   var clothDye = Color.FromArgb(0x2E, 0x30, 0x45);
   var clothSeen = new[] { Color.FromArgb(0x22, 0x2F, 0x4A), Color.FromArgb(0x23, 0x2F, 0x4B), Color.FromArgb(0x24, 0x2E, 0x4E) };
   var blueDye = Color.FromArgb(0x00, 0x2A, 0x4B);
   // 取樣裡刻意不含 #002A4B（＝色碼本身）。它離另外兩筆 5.55／5.57，而那兩筆彼此只差 1.36——
   // 是個四倍的離群值，也就是沒有被遮蔽的全亮像素，性質和布的 #415A87(高亮) 一樣。
   // 留著它會讓皮革的散布從 1.4 變成 5.5，進而推出「皮革要放到 6」和「皮革分不開布」
   // 這兩個結論，兩個都是這一筆造成的假象。
   var blueSeen = new[] { Color.FromArgb(0x03, 0x1E, 0x45), Color.FromArgb(0x0A, 0x21, 0x4B) };
   var metalDye = Color.FromArgb(0x98, 0x97, 0x9F);
   // 金屬原始四筆裡的 #566286(晚上, ΔE 23.5) 與 #767B93(晚上、高亮, ΔE 12.1) 不當材質資料——
   // 那是畫面渲染造成的明暗差（明度 63 掉到 42／52），和選色無關。
   // 但它們沒有被丟掉，第 5 段拿它們量安全邊界（金屬離布的 38.7 有 93% 是明度差）。
   var metalSeen = new[] { Color.FromArgb(0x97, 0x96, 0x9D), Color.FromArgb(0x94, 0x94, 0x99) };

   // 1. 染色色碼直接可以當掃描目標——這是色相辦不到的。實測色碼到畫面最遠 5.6。
   Check(clothSeen.Max(c => Perceptual.Distance(clothDye, c)) < 6f, "The cloth dye code is within dE 6 of its screen colours");
   Check(blueSeen.Max(c => Perceptual.Distance(blueDye, c)) < 7f, "The blue leather dye code is within dE 7 of its screen colours");
   Check(metalSeen.Max(c => Perceptual.Distance(metalDye, c)) < 3f, "The metal dye code is within dE 3 of its screen colours");
   // 2. 你說布與皮革「看起來協調且相近」——量出來畫面顏色相距 ΔE 5~7，色碼相距 ΔE 10。
   //    所以協調感在畫面顏色上，不在染色色碼上。
   float nearestSeen = clothSeen.Min(c => blueSeen.Min(d => Perceptual.Distance(c, d)));
   Check(nearestSeen > 4f && nearestSeen < 9f, "Cloth and leather look coordinated on screen, at a few dE apart");
   Check(Perceptual.Distance(clothDye, blueDye) > nearestSeen, "Their dye codes sit further apart than their screen colours do");
   // 預設誤差刻意不涵蓋這個距離：預設是 6，而這兩個色碼差 10.0。
   // 曾經為了「一個色碼同時掃到兩種材質」把預設設成 12，那個用法拿掉了，理由寫在
   // ColorMatch.DefaultTolerance 上面。這裡改成釘住「預設就是掃不到別的材質」，
   // 因為那正是拿掉之後該有的行為。
   Check(!new ColorMatch(clothDye, ColorMatch.DefaultTolerance).Hit(blueDye),
    "The default tolerance deliberately does not reach another material's dye code");
   // 真要那個寬度的話 11 是最小整數：實測 10.0179，設 10 剛好差 0.02 掃不到
   //（和當初 CIE76 算 12.33、誤差 12 差 0.33 掃不到是同一個陷阱）。
   Check(!new ColorMatch(clothDye, 10).Hit(blueDye), "Tolerance 10 is NOT enough: the distance is 10.02, short by 0.02");
   Check(new ColorMatch(clothDye, 11).Hit(blueDye), "Eleven is the smallest whole tolerance that would reach it");

   // 3. 誤差該設多少，取決於顏色是從哪裡來的——這是這個工具最重要的一條使用結論。
   //    以布的畫面取樣為中心，實測距離是一道很乾淨的階梯：
   //      布自己 0～2.4 ／ 皮革 5.3 ／ 金屬 38.7
   //
   //      填染色色碼   預設 6。蓋住自己那件東西需要 布 4.71／皮革 5.57／金屬 2.05，
   //                   所以 6 三個都夠，5 會讓皮革差 0.57 漏掉自己。
   //      吸畫面顏色   可以收到 3：蓋住自己(布 1.8／皮革 1.4／金屬 1.5)又排除別人(最近 5.3)。
   //                   任一筆取樣當中心都成立。
   //      上限         15。中性深灰 #474747 離布只有 15.7，暗處金屬 17.4——
   //                   誤差 16 就開始命中介面、陰影、地面那種灰，那就是破圖。
   //
   //    皮革有一個例外，而且不是參數能解決的：皮革色碼離自己畫面 5.57，離布的畫面卻是
   //    5.55，布比它自己還近。所以填皮革色碼時任何門檻都分不開布，要分開只能吸色。
   var cloth = new ColorMatch(clothSeen[1], ColorMatch.DefaultTolerance);
   foreach (var seen in clothSeen) Check(cloth.Hit(seen), "The default covers the cloth's own shading: " + ColorRule.Format(seen));
   foreach (var seen in metalSeen) Check(!cloth.Hit(seen), "At the default tolerance the cloth rule refuses the metal: " + ColorRule.Format(seen));
   // 注意預設值是給「填色碼」的。同一個 6 套在吸來的顏色上反而偏寬——以 #232F4B 為中心，
   // 皮革的 #0A214B 只有 5.99，會被收進來。這正是吸色要收到 3 的理由，不是 bug。
   Check(cloth.Hit(blueSeen[1]), "Six is wide for an eyedropped centre: the leather at 5.99 still gets in");
   Check(!new ColorMatch(clothSeen[1], 3).Hit(blueSeen[1]), "Which is exactly why eyedropped colours go down to 3");
   // 預設值必須讓每個材質的色碼都找得到自己那件東西，而 5 做不到（皮革 5.57）。
   foreach (var pair in new[] { Tuple.Create(clothDye, clothSeen), Tuple.Create(blueDye, blueSeen), Tuple.Create(metalDye, metalSeen) })
    foreach (var seen in pair.Item2)
     Check(new ColorMatch(pair.Item1, ColorMatch.DefaultTolerance).Hit(seen), "The default lets a dye code find its own item: " + ColorRule.Format(seen));
   Check(blueSeen.Any(c => !new ColorMatch(blueDye, 5).Hit(c)), "Five would miss the leather's own item, which is why the default is 6");
   // 介面提示的「吸色 3」必須對每個材質、每一筆取樣當中心都成立，所以全部跑一遍。
   foreach (var group in new[] { Tuple.Create(clothSeen, blueSeen.Concat(metalSeen).ToArray()),
                                 Tuple.Create(blueSeen, clothSeen.Concat(metalSeen).ToArray()),
                                 Tuple.Create(metalSeen, clothSeen.Concat(blueSeen).ToArray()) })
    foreach (var centre in group.Item1)
    {
     var rule = new ColorMatch(centre, 3);
     foreach (var mine in group.Item1) Check(rule.Hit(mine), "Eyedropping at dE 3 covers its own material: " + ColorRule.Format(centre) + " -> " + ColorRule.Format(mine));
     foreach (var other in group.Item2) Check(!rule.Hit(other), "And excludes the other materials: " + ColorRule.Format(centre) + " vs " + ColorRule.Format(other));
    }
   // 填色碼時 3 不夠，布需要 5——這是提示把兩種來源分開寫的理由。
   Check(clothSeen.Any(c => !new ColorMatch(clothDye, 4).Hit(c)), "Tolerance 4 is not enough when the colour came from a dye code");
   var byCode = new ColorMatch(clothDye, 5);
   foreach (var seen in clothSeen) Check(byCode.Hit(seen), "Five covers the cloth when the dye code is filled in instead: " + ColorRule.Format(seen));
   foreach (var seen in blueSeen) Check(!byCode.Hit(seen), "And still refuses the leather from the dye code: " + ColorRule.Format(seen));
   // 皮革色碼分不開布，而且是因為布和它自己的畫面顏色幾乎一樣近（5.55 對 5.57）——
   // 不是誤差沒調好，任何門檻都無法只留一邊。釘成「兩者相差不到 0.1」而不是大小比較，
   // 因為這本來就是個平手，用大小比較會變成在釘浮點數的最後一位。
   float toOwn = blueSeen.Max(c => Perceptual.Distance(blueDye, c));
   float toCloth = clothSeen.Min(c => Perceptual.Distance(blueDye, c));
   Check(Math.Abs(toOwn - toCloth) < .1f, "The leather dye code is as close to the cloth on screen as to its own leather");

   // 5. 金屬離布/皮革 38.7，但那個距離 93% 是明度差（金屬 L 62.4、布 L 19.6），不是色相差。
   //    明度會隨畫面變暗，所以 38.7 不是固定的安全距離——用原始那兩筆夜間取樣量出縮到多少。
   //    這兩筆不當材質資料用（它們是渲染狀態），只拿來當安全邊界的檢查。
   var metalDim = new[] { Color.FromArgb(0x76, 0x7B, 0x93), Color.FromArgb(0x56, 0x62, 0x86) };
   var widest = new ColorMatch(clothSeen[1], ColorMatch.DefaultTolerance);
   foreach (var dim in metalDim)
    Check(!widest.Hit(dim), "The default tolerance still refuses metal even when the screen goes dark: " + ColorRule.Format(dim));
   // 上限確實存在，而且比暗處金屬更早：一個跟布毫無關係的中性深灰 #474747 只有 15.7。
   // 這種灰在介面、陰影、地面到處都是，所以說明寫「不要超過 15」——這就是破圖的起點，
   // 不是一個感覺問題。誤差 16 收得到它，15 收不到。
   var greyNoise = Color.FromArgb(0x47, 0x47, 0x47);
   Check(new ColorMatch(clothSeen[1], 16).Hit(greyNoise), "At tolerance 16 an unrelated neutral grey starts matching, which is why 15 is the ceiling");
   Check(!new ColorMatch(clothSeen[1], 15).Hit(greyNoise), "At 15 that grey is still out");
   Check(Perceptual.Distance(clothSeen[1], greyNoise) < metalDim.Min(d => Perceptual.Distance(clothSeen[1], d)),
    "The grey noise floor is tighter than the dim metal, so it is what sets the ceiling");
   Check(new ColorMatch(clothSeen[1], 18).Hit(metalDim[1]), "And by tolerance 18 even the dim metal gets caught");
   Check(metalDim.Min(d => Perceptual.Distance(clothSeen[1], d)) < metalSeen.Min(d => Perceptual.Distance(clothSeen[1], d)),
    "Dimming metal moves it closer to the cloth, so 38.7 is a normal-state figure only");

   // 6. 這個工具不做「用誤差去配色」。三個材質的色碼兩兩距離是 10.0（布↔皮革）、
   //    38.7（布↔金屬）、42.7（皮革↔金屬），而破圖的上限只有 15——
   //    也就是說連最近的那一對都要 11 才搆得到，金屬那兩對根本沒有可用值。
   //    釘住這個，免得有人又把預設值往上調去試「跨材質選色」。
   const int Ceiling = 15;
   Check(Perceptual.Distance(clothDye, metalDye) > Ceiling && Perceptual.Distance(blueDye, metalDye) > Ceiling,
    "Metal sits past the grey-noise ceiling from both other dye codes, so no tolerance reaches it");
   Check(Perceptual.Distance(clothDye, blueDye) > ColorMatch.DefaultTolerance,
    "Even the closest pair of dye codes is beyond the default, so matching materials is not what the tolerance is for");

   // 7. DistanceTo 回報離目標色多遠，比「命中／不命中」多了程度資訊。
   Check(Math.Abs(cloth.DistanceTo(clothSeen[1])) < .01f, "The target colour is zero distance from its own rule");
   Check(cloth.DistanceTo(metalSeen[0]) > 20f, "An unrelated colour reports a large distance");
   // 比對器把自己的目標色與高亮色一起帶著走，介面與繪製都從這裡取，不再有平行清單。
   Check(cloth.Target == clothSeen[1] && cloth.Highlight == ColorRule.Contrast(clothSeen[1]), "A matcher carries its own target and highlight");
  }

  // 自動對比色：必須和目標色差得夠遠，否則掃描會吸到自己畫的高亮而閃動。
  foreach (var sample in new[] { Color.Red, Color.Lime, Color.Blue, Color.White, Color.Black, Color.FromArgb(128, 128, 128), Color.FromArgb(0, 0, 128), Color.FromArgb(214, 64, 64) })
  {
   var contrast = ColorRule.Contrast(sample);
   Check(ColorScanner.Difference(contrast.ToArgb(), sample) > 100, "Contrast colour is far from " + ColorRule.Format(sample));
   // 高亮的真正要求是「看得出來」，那正是感知距離在說的事，所以也用 ΔE 釘一次。
   Check(Perceptual.Distance(contrast, sample) > 27f, "Contrast colour is perceptually far from " + ColorRule.Format(sample));
   Check(contrast.A == 255, "Contrast colour is opaque");
  }
  Check(ColorRule.Contrast(Color.Red).B > 150 && ColorRule.Contrast(Color.Red).R < 60, "Red picks a cyan-side highlight");
  Check(ColorRule.Contrast(Color.Lime).R > 150 && ColorRule.Contrast(Color.Lime).G < 60, "Green picks a magenta-side highlight");
  Check(ColorRule.Contrast(Color.Black) != ColorRule.Contrast(Color.White), "Near-greys split by brightness");
  Check(ColorRule.Contrast(Color.FromArgb(24, 24, 24)) == ColorRule.Contrast(Color.Black), "Near-greys share the dark fallback");
  var target = Color.FromArgb(200, 40, 40);
  var pixels = Canvas(32, 32, Color.White, new Rectangle(8, 8, 8, 8), target);
  var result = ColorScanner.Scan(pixels, 32, 32, One(target, 16), 2, 4);
  Check(result.Counts[0] == 4 && result.Hits.Count == 2, "Matched block merges into per-row runs");
  Check(result.Hits.All(h => h.Bounds.Width == 8 && h.Bounds.Height == 4), "Runs cover the patch width");
  Check(result.Centers[0] == new Point(12, 12), "Centroid of the matched patch");
  Check(ColorScanner.Scan(pixels, 32, 32, One(target, 0), 2, 4).Counts[0] == 4, "Exact color matches with zero tolerance");
  Check(ColorScanner.Scan(pixels, 32, 32, One(Color.FromArgb(0, 0, 255), 10), 2, 4).Total == 0, "Unrelated color finds nothing");
  // 上方色碼組的容差涵蓋下方的目標色時，下方那組就完全沒有命中（容差預覽會把這件事算出來講明）。
  Check(ColorScanner.Scan(pixels, 32, 32, One(Color.White, 100).Concat(One(target, 16)).ToList(), 2, 4).Counts[1] == 0, "A wide first rule swallows the rules below it");
  Check(ColorScanner.Scan(pixels, 32, 32, One(target, 16).Concat(One(Color.White, 100)).ToList(), 2, 4).Counts[0] == 4, "Reordering gives the narrower rule its hits back");
  Check(ColorScanner.Scan(pixels, 32, 32, One(target, 16), 8, 4).Counts[0] < 4, "Coarse sampling misses cells");
  Check(ColorScanner.Scan(pixels, 32, 32, new List<ColorMatch>(), 2, 4).Total == 0, "Empty rule list scans nothing");
  // 改成單一 ColorMatch 清單之後，「目標色與容差數量不一致」這種錯誤在結構上就不可能發生了，
  // 剩下要防的只有整份清單是 null。
  bool missing = false; try { ColorScanner.Scan(pixels, 32, 32, null, 2, 4); } catch { missing = true; }
  Check(missing, "A missing rule list is refused");
  var edge = ColorScanner.Scan(Canvas(10, 10, Color.White, new Rectangle(0, 0, 10, 10), target), 10, 10, One(target, 0), 1, 4);
  Check(edge.Hits.All(h => h.Bounds.Right <= 10 && h.Bounds.Bottom <= 10), "Blocks clip to the scan area");
  // 多組規則共用一次掃描：每個像素的 Lab 只算一次，但每組規則都要拿到自己的命中。
  {
   var brown = Color.FromArgb(139, 90, 43);
   var two = new List<ColorMatch> { new ColorMatch(brown, 10), new ColorMatch(target, 6) };
   var canvas = Canvas(16, 16, brown, new Rectangle(0, 0, 8, 16), target);
   var both = ColorScanner.Scan(canvas, 16, 16, two, 1, 4);
   Check(both.Counts[0] > 0 && both.Counts[1] > 0, "Several rules can share one scan");
   Check(both.Counts[0] + both.Counts[1] == 16, "Every cell goes to exactly one rule");
  }

  using (var overlay = new ScanOverlay())
  {
   overlay.ScanArea = new Rectangle(300, 200, 320, 240);
   Check(overlay.ScanArea == new Rectangle(300, 200, 320, 240), "Scan area excludes the drag band");
   Check(overlay.Bounds == new Rectangle(300 - ScanOverlay.Band, 200 - ScanOverlay.Band, 320 + ScanOverlay.Band * 2, 240 + ScanOverlay.Band * 2), "Chrome sits outside the scanned pixels");
   var frozen = overlay.ScanArea;
   Check(!overlay.BeginDrag(new Point(ScanOverlay.Band / 2, overlay.ClientSize.Height / 2)), "A locked box refuses to start a drag");
   Check(!overlay.DragTo(new Size(40, -25)) && overlay.ScanArea == frozen, "A locked box cannot be moved by the mouse at all");
   overlay.Locked = false;
   Check(overlay.BeginDrag(new Point(ScanOverlay.Band / 2, overlay.ClientSize.Height / 2)), "Edit mode accepts a border drag");
   Check(overlay.DragTo(new Size(40, -25)) && overlay.ScanArea == new Rectangle(340, 175, 320, 240), "Border drag moves the box without resizing");
   overlay.EndDrag();
   Check(overlay.ZoneAt(new Point(2, 2)) == 1 && overlay.ZoneAt(new Point(overlay.ClientSize.Width - 2, overlay.ClientSize.Height - 2)) == 4 && overlay.ZoneAt(new Point(ScanOverlay.Band / 2, overlay.ClientSize.Height / 2)) == 5, "Corner grips and move band");
   var bounds = new Rectangle(100, 100, 200, 200);
   Check(ScanOverlay.Transform(bounds, 4, new Size(30, 40)) == new Rectangle(100, 100, 230, 240), "Bottom-right grip resizes");
   Check(ScanOverlay.Transform(bounds, 1, new Size(30, 40)) == new Rectangle(130, 140, 170, 160), "Top-left grip moves the origin");
   Check(ScanOverlay.Transform(bounds, 1, new Size(9000, 9000)).Width == ScanOverlay.MinSide + ScanOverlay.Band * 2, "Resize keeps the minimum size");
   Check(ScanOverlay.Transform(bounds, 5, new Size(-15, 7)) == new Rectangle(85, 107, 200, 200), "Move keeps the size");
   var scanned = Color.FromArgb(214, 64, 64);
   overlay.Matches = new List<ColorMatch> { new ColorMatch(scanned, ColorMatch.DefaultTolerance) };
   // 「掃描讀不到自己畫的高亮」是必要條件，而達成它有兩條路，取捨正好相反——
   // 這個勾選框就是在兩者之間選。NeedsBlank 是純函式，先把真值表釘住。
   Check(!ScanOverlay.NeedsBlank(0, false, true), "With no hits nothing covers the region, so no blank is needed");
   Check(ScanOverlay.NeedsBlank(3, false, true), "With hits on screen the box must blank before scanning");
   Check(!ScanOverlay.NeedsBlank(3, true, true), "Already blank means the next step is the scan itself");
   Check(!ScanOverlay.NeedsBlank(3, false, false), "Excluded from capture, the scan cannot see itself, so it never blanks");
   overlay.SeedHits(new ScanHit { Rule = 0, Bounds = new Rectangle(0, 0, 4, 4) });
   Check(!overlay.Blanked && overlay.Phase.Count == 1 && overlay.Phase[0] == ColorRule.Contrast(scanned), "The lit phase paints the single highlight colour");
   overlay.Step();
   Check(overlay.Blanked && overlay.Phase.Count == 0, "The dark phase paints nothing, so the original colour shows through");

   const int slow = 800;
   overlay.Interval = slow;
   // 閃爍開啟：亮暗各一個完整間隔，掃描頻率減半，但使用者自己截圖拍得到高亮。
   overlay.Blink = true;
   Check(overlay.NeedsBlanking, "Blinking always needs the blank phase, that is what blinking is");
   Check(overlay.BlankLength == slow && overlay.LitLength == slow, "Blinking gives each phase a whole interval");
   // 閃爍關閉且系統接受排除擷取：完全不進暗相位，週期就是一個完整間隔。
   overlay.Blink = false;
   overlay.SimulateCaptureExcluded(true);
   Check(!overlay.NeedsBlanking, "Excluded from capture means no blank phase at all");
   Check(overlay.LitLength == slow, "So the whole interval is lit and the scan runs at full rate");
   // 系統不接受（Win10 2004 之前）就必須退回暗相位——否則掃描會讀到自己。
   // 這是「關掉閃爍反而閃得更快」那個舊行為僅存的地方，而且現在只在真的別無選擇時發生。
   overlay.SimulateCaptureExcluded(false);
   Check(overlay.NeedsBlanking, "Without the flag the blank phase is the only way, so it comes back");
   Check(overlay.BlankLength == ScanOverlay.BlankWindow && overlay.LitLength == slow - ScanOverlay.BlankWindow, "The fallback keeps only the minimum blank the scan needs");
   Check(overlay.BlankLength + overlay.LitLength == overlay.Interval, "And its cycle is exactly one scan interval");
   // 間隔的預設值同時也是下限，而且必須容得下暗相位。
   Check(ScanOverlay.DefaultInterval > ScanOverlay.BlankWindow, "The interval floor always leaves room for the blank phase");
   overlay.Interval = 1;
   Check(overlay.Interval == ScanOverlay.DefaultInterval, "Too small an interval is clamped to the floor");
  }
  // 吸色色票永遠和游標錯開，所以不會蓋住正在取樣的那一個像素。
  var bubbleSize = ColorBubble.Preferred; var screenArea = new Rectangle(0, 0, 1920, 1080);
  Check(ColorBubble.Place(screenArea, new Point(400, 400), bubbleSize) == new Point(424, 424), "Colour bubble sits below-right of the cursor");
  Check(ColorBubble.Place(screenArea, new Point(1910, 1070), bubbleSize).X == 1910 - 24 - bubbleSize.Width, "Colour bubble flips away from the screen edge");
  var placed = ColorBubble.Place(screenArea, new Point(4, 4), bubbleSize);
  Check(placed.X >= 0 && placed.Y >= 0, "Colour bubble stays on screen");
  Check(!new Rectangle(placed, bubbleSize).Contains(new Point(4, 4)), "Colour bubble never covers the sampled pixel");
  using (var idle = new ColorPicker(null)) Check(!idle.Running, "A picker installs no hook until it starts");

  var profile = new ScanProfile { X = 10, Y = 20, Width = 200, Height = 150, Interval = ScanOverlay.DefaultInterval, Sample = 2, Block = 4, Rules = new List<ColorRule> { new ColorRule { Target = "#123456", Tolerance = 30 } } };
  ScanProfile.Validate(profile);
  var copy = Json.Copy(profile);
  ScanProfile.Validate(copy);
  Check(copy.Rules[0].Target == "#123456" && copy.Rules[0].Tolerance == 30 && copy.Width == 200, "Scan profile roundtrip");
  // 改版前的設定檔只有 Target 與 Tolerance，所以結構上完全相容，不會掉資料。
  // 唯一的差別是 Tolerance 的單位從「通道差」變成 ΔE，數字照讀，升級後要重新調一次。
  {
   var old = Json.Read<ScanProfile>("{\"Kind\":\"MacroColorScan\",\"X\":5,\"Y\":6,\"Width\":200,\"Height\":150,\"Interval\":" + ScanOverlay.DefaultInterval + ",\"Sample\":2,\"Block\":4,\"Blink\":true,\"Rules\":[{\"Target\":\"#AABBCC\",\"Tolerance\":18}]}");
   ScanProfile.Validate(old);
   Check(old.Rules[0].Tolerance == 18 && old.Rules[0].Target == "#AABBCC", "An old profile still loads, keeping its colours and its number");
   // 舊檔還留著已經移除的欄位，多出來的屬性必須被忽略而不是讓整份設定爆掉。
   var extra = Json.Read<ScanProfile>("{\"Kind\":\"MacroColorScan\",\"X\":5,\"Y\":6,\"Width\":200,\"Height\":150,\"Interval\":" + ScanOverlay.DefaultInterval + ",\"Sample\":2,\"Block\":4,\"Rules\":[{\"Target\":\"#AABBCC\",\"Tolerance\":18,\"Mode\":\"hue\",\"HueTolerance\":10,\"SatTolerance\":15}]}");
   ScanProfile.Validate(extra);
   Check(extra.Rules[0].Tolerance == 18, "Fields that no longer exist are ignored rather than fatal");
   Check(new ColorRule().Tolerance == ColorMatch.DefaultTolerance, "A fresh rule starts at the default perceptual tolerance");
  }
  // 誤差的上下限由 ColorMatch 定義，驗證與欄位都引用同一組常數。
  Check(ColorMatch.MinTolerance == 0 && ColorMatch.MaxTolerance > ColorMatch.DefaultTolerance, "The tolerance range brackets the default");
  // 一組只有一個色碼。逗號分隔的多重樣本移除了——真要同時盯住幾個顏色就開幾組規則，
  // 每組有自己的誤差和自己的高亮色，比幾個顏色共用一個高亮色好用。
  {
   bool rejected = false; try { ColorRule.Parse("#FF0000, #00FF00"); } catch { rejected = true; }
   Check(rejected, "A rule holds one colour, so a comma separated list is not a colour");
  }
  foreach (var badRule in new[] { new ColorRule { Target = "#FF0000", Tolerance = ColorMatch.MaxTolerance + 1 }, new ColorRule { Target = "#FF0000", Tolerance = -1 }, new ColorRule { Target = "#FF0000, nonsense" } })
  {
   bool rejected = false; try { ColorRule.Validate(badRule); } catch { rejected = true; }
   Check(rejected, "Invalid rule accepted");
  }

  // 容差極限預覽：這是「誤差到底放進了多少色偏」的檢查工具，所以它自己也要被釘住。
  {
   var rule = new ColorRule { Target = "#232F4B", Tolerance = ColorMatch.DefaultTolerance };
   var groups = TolerancePreview.Extremes(rule);
   Check(groups.Count == 2, "A single-sample rule shows the three Lab axes and the eight corners");
   Check(groups[0].Samples.Count == 7, "The axis row shows the target plus six directions");
   Check(groups[1].Samples.Count == 8, "The corner row shows all eight combined directions");
   var compiled = ColorMatch.Compile(rule);
   // 預覽標成「界內」的色票必須真的命中——這個工具的價值全靠這一點，它一旦說謊就毫無意義。
   Check(groups.SelectMany(s => s.Samples).All(s => s.Inside == compiled.Hit(s.Colour)), "The preview's inside/outside marks agree with the real matcher");
   // Edge() 從邊界往目標色退，所以每一格都必須是真的會命中的顏色。直接取邊界值行不通：
   // sRGB 只覆蓋 Lab 空間的一小塊，推出去的座標會被夾回色域，夾過的距離就變了。
   Check(groups.SelectMany(s => s.Samples).All(s => s.Inside), "Every colour the preview draws really is inside the tolerance");
   // 兩條性質一起才算「這個預覽沒有騙人」：
   //   每一格都在門檻內（否則畫出來的不是命中的顏色）
   //   最遠的那一格要真的貼到門檻（否則 Edge() 退太多，預覽低估了實際範圍）
   //
   // 刻意不要求「每一格」都貼到門檻：sRGB 只覆蓋 Lab 空間的一小塊，貼著色域邊界的顏色
   // 有些方向根本走不出去——純紅再往紅推就出了色域，那個方向的極限色就是純紅自己。
   // 那不是 bug，是「sRGB 裡沒有比純紅更紅這麼多 ΔE 的顏色」這個事實。
   var all = groups[0].Samples.Concat(groups[1].Samples).ToArray();
   Check(all.All(s => compiled.DistanceTo(s.Colour) <= compiled.Tolerance + .01f), "No extreme sits outside the tolerance");
   Check(all.Max(s => compiled.DistanceTo(s.Colour)) > compiled.Tolerance * .85f, "The furthest extreme really does reach the boundary");
   // 色域邊角上的顏色也不能讓預覽壞掉或回報界外的色票。
   foreach (var edgeCase in new[] { "#FF0000", "#00FF00", "#0000FF", "#000000", "#FFFFFF", "#0A0A0A" })
   {
    var corner = new ColorRule { Target = edgeCase, Tolerance = ColorMatch.DefaultTolerance };
    var cornerMatch = ColorMatch.Compile(corner);
    var drawn = TolerancePreview.Extremes(corner).SelectMany(s => s.Samples).ToArray();
    Check(drawn.Length > 0 && drawn.All(s => s.Inside && cornerMatch.Hit(s.Colour)), "Gamut corners still preview only real matches: " + edgeCase);
   }
   // 誤差 0 只認得目標色本身，那一排會被去重併成一格。
   Check(TolerancePreview.Extremes(new ColorRule { Target = "#232F4B", Tolerance = 0 })[0].Samples.Count == 1, "With no tolerance only the target itself matches");
   Check(TolerancePreview.Extremes(new ColorRule { Target = "壞掉的色碼" }).Count == 0, "A half-typed rule previews nothing rather than throwing");
   // 逗號清單不再是合法色碼（一組只有一個顏色），所以它和其他半成品走同一條路：預覽空白，不丟例外。
   Check(TolerancePreview.Extremes(new ColorRule { Target = "#232F4B, #415A87" }).Count == 0, "A comma separated list is not a colour any more");
   Check(TolerancePreview.Describe(rule).Contains("ΔE"), "The description names the metric");
   // 規則重疊：上方那組吃掉下方那組，是使用者實際會踩到的坑。
   var wide = new ColorRule { Target = "#808080", Tolerance = ColorMatch.MaxTolerance };
   var narrow = new ColorRule { Target = "#232F4B", Tolerance = 4 };
   Check(TolerancePreview.Overlaps(new[] { wide, narrow }).Count == 1, "A rule swallowed by the one above it is reported");
   Check(TolerancePreview.Overlaps(new[] { narrow, wide }).Count == 0, "The narrow rule above the wide one is fine");
   // 一組只有一個顏色，所以重疊不再有「部分」——只有「被吃掉」與「沒被吃掉」。
   Check(TolerancePreview.Overlaps(new[] { narrow, new ColorRule { Target = "#242E4E", Tolerance = 4 } }).Count == 1, "A rule whose colour sits inside the one above is reported");
   Check(TolerancePreview.Overlaps(new[] { narrow, new ColorRule { Target = "#C86464", Tolerance = 4 } }).Count == 0, "A distant rule is not a conflict");
   // 填壞的色碼不能讓重疊檢查爆掉，它只是無法參與比較。
   Check(TolerancePreview.Overlaps(new[] { narrow, new ColorRule { Target = "壞掉" } }).Count == 0, "An unparseable rule is skipped rather than fatal");
   Check(TolerancePreview.Overlaps(null).Count == 0, "No rules means no warnings");
   // 版面計算同時服務捲動高度與實際繪製，所以 null 那條路徑必須算出同一個高度。
   int measured = TolerancePreview.Layout(880, new[] { rule, narrow }, null);
   Check(measured > 200, "The preview reports the height it needs for scrolling");
   using (var page = new Bitmap(880, measured)) using (var canvas = Graphics.FromImage(page))
   {
    Check(TolerancePreview.Layout(880, new[] { rule, narrow }, canvas) == measured, "Measuring and drawing agree on the height");
    page.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-tolerance.png"));
   }
  }
  foreach (var broken in new[] { new ScanProfile { Interval = ScanOverlay.DefaultInterval - 1 }, new ScanProfile { Sample = 0 }, new ScanProfile { Block = 0 }, new ScanProfile { Width = 4, Height = 4 } })
  {
   bool rejected = false; try { ScanProfile.Validate(broken); } catch { rejected = true; }
   Check(rejected, "Invalid scan setting accepted");
  }
  {
   bool rejected = false; var tooMany = new ScanProfile(); for (int i = 0; i < 21; i++) tooMany.Rules.Add(new ColorRule());
   try { ScanProfile.Validate(tooMany); } catch { rejected = true; }
   Check(rejected, "Rule count limit");
  }
  string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "save-tests-scan-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
  string file = Path.Combine(folder, "scan-profile.json");
  Check(ScanSession.Load(file).Rules.Count == 1, "Missing profile falls back to a centered default");
  ScanSession.Save(file, profile); Check(ScanSession.Load(file).Rules[0].Target == "#123456", "Saved profile reloads");
  profile.Rules[0].Tolerance = 99; ScanSession.Save(file, profile); Check(ScanSession.Load(file).Rules[0].Tolerance == 99, "Profile overwrite keeps the latest values");
  File.WriteAllText(file, "{\"Kind\":\"Other\"}"); bool wrongKind = false; try { ScanSession.Load(file); } catch { wrongKind = true; }
  Check(wrongKind, "Foreign profile file rejected");
  // 自動保存的位置是程式自己管理的，第一次執行時資料夾還不存在，所以要自己建起來。
  // 使用者指定的另存位置則相反——資料夾不存在就該失敗，那條路由 TestTemplateSaving 驗證。
  string nested = Path.Combine(folder, "nested", "scan-profile.json");
  ScanSession.Save(nested, profile);
  Check(File.Exists(nested), "Auto-saved settings create their own folder when missing");

  using (var studio = new ScanStudioForm(profile))
  {
   Check(studio.Rows().Count() == 1, "Profile rules load into rows");
   var added = studio.AddRule(new ColorRule { Target = "#0088FF", Tolerance = 12 });
   Check(studio.Rows().Count() == 2, "Add button appends a rule row");
   Check(added.Controls.OfType<TextBox>().Count() == 1, "A rule row only asks for the target colours");
   Check(added.Controls.OfType<NumericUpDown>().Count() == 1, "A rule row asks for exactly one number");
   Check(added.Controls.OfType<ComboBox>().Count() == 0, "There is no mode or material dropdown any more");
   Check(added.Highlight == ColorRule.Contrast(ColorRule.Parse("#0088FF")), "The row reports the automatic contrast highlight");
   // 誤差的上下限直接引用 ColorMatch 的常數，欄位收得進去的值一定通得過 Validate。
   Check(added.Tolerance.Minimum == ColorMatch.MinTolerance && added.Tolerance.Maximum == ColorMatch.MaxTolerance, "The field range matches what the rule accepts");
   added.Tolerance.Value = ColorMatch.MaxTolerance;
   ColorRule.Validate(added.Value);
   added.Tolerance.Value = 40;
   Check(added.Value.Tolerance == 40, "The field writes straight through to the rule");
   added.Tolerance.Value = 12;
   // 半成品色碼只是暫時跳過這一列，不會中斷掃描也不會丟例外。
   added.Target.Text = "#0088F";
   Check(!added.IsValid && added.Compiled == null && added.Highlight == SystemColors.Control, "A half-typed colour is skipped, not fatal");
   added.Target.Text = "#0088FF";
   Check(added.IsValid && added.Compiled != null, "Fixing the colour brings the row back");
   // 換成工具視窗的中文字型後，按鈕必須跟著長大，文字不能被擠壓。
   added.PerformLayout();
   foreach (var button in added.Controls.OfType<Button>())
   {
    var needed = TextRenderer.MeasureText(button.Text, button.Font);
    Check(button.Width >= needed.Width + button.Padding.Horizontal, "Row button fits its label: " + button.Text);
   }
   Check(studio.overlay.Matches.Count == 2 && studio.overlay.Matches[1].Highlight == added.Highlight, "Rows feed the overlay");
   Check(studio.overlay.Matches[1].Hit(ColorRule.Parse("#0088FF")), "The compiled matcher carries the row's own colour");
   added.Target.Text = "不是色碼";
   Check(studio.overlay.Matches.Count == 1 && studio.Current().Rules.Count == 1, "Half-typed colours are skipped, not fatal");
   added.Target.Text = "#0088FF"; Check(studio.overlay.Matches.Count == 2, "Fixing the colour restores the rule");
   studio.areaX.Value = 640; studio.areaY.Value = 360; studio.areaW.Value = 200; studio.areaH.Value = 120;
   Check(studio.overlay.ScanArea == new Rectangle(640, 360, 200, 120), "Area fields drive the overlay");
   var box = studio.overlay; int mid = box.ClientSize.Height / 2;
   Check(box.BeginDrag(new Point(ScanOverlay.Band / 2, mid)) && box.DragTo(new Size(10, 10)), "Edit mode accepts a border drag");
   box.EndDrag();
   Check(studio.areaX.Value == 650 && studio.areaY.Value == 370 && studio.Current().X == 650, "Border drag writes back to the fields");
   studio.SetLocked(true); Check(studio.overlay.Locked && studio.lockButton.Text == "編輯掃描範圍", "Lock toggle text");
   Check(studio.GeometryFields().All(f => !f.Enabled) && !studio.centerButton.Enabled, "Lock freezes the coordinate and size fields");
   var pinned = box.ScanArea;
   Check(!box.BeginDrag(new Point(ScanOverlay.Band / 2, mid)) && !box.DragTo(new Size(5, -5)), "A locked box refuses every drag");
   Check(box.ScanArea == pinned && studio.areaX.Value == 650 && studio.areaY.Value == 370, "A locked box stays exactly where it was");
   studio.SetLocked(false); Check(!studio.overlay.Locked && studio.lockButton.Text == "鎖定掃描範圍", "Edit toggle text");
   Check(studio.GeometryFields().All(f => f.Enabled) && studio.centerButton.Enabled, "Edit mode unlocks the coordinate fields");
   var boxes = Descendants(studio).OfType<CheckBox>().ToArray();
   Check(boxes.Length == 1 && boxes[0].Text.Contains("閃爍"), "Only the blink checkbox remains on the toolbar");
   studio.blink.Checked = false; Check(!studio.overlay.Blink && studio.Current().Blink == false, "Blink toggle reaches the overlay and the profile");
   studio.blink.Checked = true; Check(studio.overlay.Blink, "Blink can be switched back on");
   studio.SetScanning(true);
   Check(studio.IsScanning && studio.overlay.Locked && studio.scanButton.Text == "結束掃描 (F8)", "Starting a scan locks the box");
   Check(!studio.lockButton.Enabled, "Scanning forces the lock, so the mode toggle is disabled");
   studio.SetScanning(false);
   Check(!studio.IsScanning && !studio.overlay.Locked && !studio.overlay.Scanning && studio.scanButton.Text == "開始掃描 (F7)", "Stopping a scan returns to edit mode");
   Check(studio.lockButton.Enabled, "Stopping a scan hands the mode toggle back");
   Check(ScanStudioForm.KeyStart == 118 && ScanStudioForm.KeyStop == 119 && ScanStudioForm.HotStart != ScanStudioForm.HotStop, "F7 starts and F8 stops with distinct hotkey ids");
   var saved = studio.Current(); ScanProfile.Validate(saved); Check(saved.Rules.Count == 2 && saved.Interval == ScanOverlay.DefaultInterval, "Studio exports a valid profile");
   Check(studio.Rows().Last().Controls.OfType<Button>().Any(b => b.Text == "移除"), "Each row offers a remove button");
   studio.RemoveRow(studio.Rows().Last());
   Check(studio.Rows().Count() == 1 && studio.overlay.Matches.Count == 1, "Remove drops the row and its colours");
   var panel = studio.Controls[0]; studio.Controls.Remove(panel); panel.Size = studio.ClientSize; panel.CreateControl(); panel.PerformLayout();
   using (var bitmap = new Bitmap(panel.Width, panel.Height)) { panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, panel.Size)); bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-scan-panel.png")); }
   panel.Dispose();
  }
  // 左右兩格是閃爍的兩個相位，方便比對辨識度。
  using (var bitmap = new Bitmap(860, 300)) using (var g = Graphics.FromImage(bitmap))
  {
   // 左右兩格是閃爍的兩個相位：亮相位畫高亮，暗相位什麼都不畫，所以看到的是目標原色。
   g.Clear(Color.FromArgb(245, 247, 250));
   var sample = Color.FromArgb(214, 64, 64);
   var patch = new Rectangle(90, 70, 150, 110);
   var demo = ColorScanner.Scan(Canvas(420, 300, Color.White, patch, sample), 420, 300, One(sample, 20), 2, 4);
   // 兩格都先畫上被掃描的目標色，代表螢幕上本來就有的東西。
   using (var brush = new SolidBrush(sample)) { g.FillRectangle(brush, patch); g.FillRectangle(brush, patch.X + 440, patch.Y, patch.Width, patch.Height); }
   ScanOverlay.PaintHits(g, Point.Empty, demo.Hits, new[] { ColorRule.Contrast(sample) });
   using (var pen = new Pen(Color.FromArgb(0, 120, 215), 2))
   {
    pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash; g.DrawRectangle(pen, 1, 1, 417, 297); g.DrawRectangle(pen, 441, 1, 417, 297);
   }
   using (var font = new Font("Microsoft JhengHei UI", 9, FontStyle.Bold)) { g.DrawString("亮相位：畫高亮", font, Brushes.DimGray, 8, 6); g.DrawString("暗相位：不畫（看到原色，掃描在此進行）", font, Brushes.DimGray, 448, 6); }
   bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-scan.png"));
  }
 }
}
