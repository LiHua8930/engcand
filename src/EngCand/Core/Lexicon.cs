using System;
using System.Collections.Generic;
using System.IO;

namespace EngCand
{
    /// <summary>词典数据容器 + 前缀索引（Trie）。由 Lead 维护。</summary>
    public sealed class Lexicon
    {
        public sealed class WordEntry
        {
            public string Word;
            public int Score;
        }

        public sealed class PhraseEntry
        {
            public string Phrase;
            public string FirstToken;
            public int Score;
        }

        public sealed class NextEntry
        {
            public string Next;      // 下一个词原文
            public string Insert;    // 选中后插入的文本（可能是 "you very much" 这类剩余部分）
            public int Score;
            public bool IsPhrase;
        }

        public List<WordEntry> Words = new List<WordEntry>();
        public List<PhraseEntry> Phrases = new List<PhraseEntry>();
        public Dictionary<string, List<NextEntry>> NextByWord =
            new Dictionary<string, List<NextEntry>>(StringComparer.OrdinalIgnoreCase);

        public int MaxWordScore = 1;
        public int MaxPhraseScore = 1;
        public bool LoadedFromFile;

        // 没有词典数据时的兜底常用词（只用于保证程序可用；正式版使用 data/words.tsv）
        static readonly string[] FallbackWords = new string[]
        {
            "the","of","and","to","a","in","for","is","on","that","by","this","with","you","it","not","or","be","are",
            "from","at","as","your","all","have","new","more","an","was","we","will","home","can","us","about","if",
            "my","has","search","free","but","our","one","other","do","no","information","time","they","site","he","up",
            "may","what","which","their","news","out","use","any","there","see","only","so","his","when","contact","here",
            "business","who","web","also","now","help","get","view","online","first","am","been","would","how","were",
            "me","services","some","these","click","its","like","service","than","find","price","date","back","top",
            "people","had","list","name","just","over","state","year","day","into","email","two","health","world","next",
            "used","go","work","last","most","products","music","buy","data","make","them","should","product","system",
            "post","her","city","add","policy","number","such","please","available","copyright","support","message","after",
            "best","software","then","good","video","well","where","info","rights","public","books","high","school",
            "through","each","links","she","review","years","order","very","privacy","book","items","company","read",
            "group","need","many","user","said","does","set","under","general","research","university","mail","full",
            "map","reviews","program","life","know","games","way","days","management","part","could","great","united",
            "hotel","real","item","international","center","must","store","travel","comments","made","development",
            "report","off","member","details","line","terms","before","hotels","did","send","right","type","because",
            "local","those","using","results","office","education","national","car","design","take","posted","internet",
            "address","community","within","states","area","want","phone","shipping","reserved","subject","between",
            "forum","family","long","based","code","show","even","black","check","special","prices","website","index",
            "being","women","much","sign","file","link","open","today","technology","south","case","project","same",
            "pages","version","section","own","found","sports","house","related","security","both","county","american",
            "photo","game","members","power","while","care","network","down","computer","systems","three","total",
            "place","end","following","download","him","without","per","access","think","north","resources","current",
            "posts","big","media","law","control","water","history","pictures","size","art","personal","since",
            "including","guide","shop","directory","board","location","change","white","text","small","rating","rate",
            "government","children","during","return","students","shopping","account","times","sites","level","digital",
            "profile","previous","form","events","love","old","main","call","hours","image","department","title",
            "description","insurance","another","why","shall","property","class","still","money","quality","every",
            "listing","content","country","private","little","visit","save","tools","low","reply","customer","compare",
            "movies","include","college","value","article","man","card","jobs","provide","food","source","author",
            "different","press","learn","sale","around","print","course","job","canada","process","room","stock",
            "training","too","credit","point","join","science","men","categories","advanced","west","sales","look",
            "english","left","team","estate","box","conditions","select","windows","photos","thread","week","category",
            "note","live","large","gallery","table","register","however","market","library","really","action","start",
            "series","model","features","air","industry","plan","human","provided","yes","required","second","hot",
            "cost","movie","forums","march","better","say","questions","july","going","medical","test","friend","come",
            "server","study","application","cart","staff","articles","feedback","again","play","looking","issues",
            "april","never","users","complete","street","topic","comment","financial","things","working","against",
            "standard","tax","person","below","mobile","less","got","blog","party","payment","equipment","login",
            "student","let","programs","offers","legal","above","recent","park","stores","side","act","problem","red",
            "give","memory","performance","social","august","quote","language","story","sell","options","experience",
            "rates","create","key","body","young","america","important","field","few","east","paper","single","age",
            "activities","club","example","girls","additional","password","latest","something","road","gift",
            "question","changes","night","hard","pay","four","status","browse","issue","range","building","seller",
            "court","february","always","result","audio","light","write","war","offer","blue","groups","easy","given",
            "files","event","release","analysis","request","fax","china","making","picture","needs","possible","might",
            "professional","yet","month","major","star","areas","future","space","committee","hand","sun","cards",
            "problems","london","washington","meeting","become","interest","child","keep","enter","california","share",
            "similar","garden","schools","million","added","reference","companies","listed","baby","learning","energy",
            "run","delivery","net","popular","term","film","stories","put","computers","journal","reports","try",
            "welcome","central","images","president","notice","god","original","head","radio","until","cell","color",
            "self","council","away","includes","track","australia","discussion","archive","once","others","entertainment",
            "agreement","format","least","society","months","log","safety","friends","sure","faq","trade","edition",
            "cars","messages","marketing","tell","further","updated","association","able","having","provides","david",
            "fun","already","green","studies","close","common","drive","specific","several","gold","living","collection",
            "called","short","arts","lot","ask","display","limited","powered","solutions","means","director","daily",
            "beach","past","natural","whether","due","electronics","five","upon","period","planning","database","says",
            "official","weather","land","average","done","technical","window","france","pro","region","island","record",
            "direct","microsoft","conference","environment","records","district","calendar","costs","style","url",
            "front","statement","update","parts","ever","downloads","early","miles","sound","resource","present",
            "applications","either","ago","document","word","works","material","bill","written","talk","federal",
            "hosting","rules","final","adult","tickets","thing","centre","requirements","via","cheap","kids","finance",
            "true","minutes","else","mark","third","rock","gifts","europe","reading","topics","bad","individual",
            "tips","plus","auto","cover","usually","edit","together","videos","percent","fast","function","fact",
            "unit","getting","global","tech","meet","far","economic","player","projects","lyrics","often","subscribe",
            "submit","germany","amount","watch","included","feel","though","bank","risk","thanks","everything",
            "deals","various","words","linux","production","commercial","james","weight","town","heart","advertising",
            "received","choose","treatment","newsletter","archives","points","knowledge","magazine","error","camera",
            "girl","currently","construction","registered","clear","golf","receive","domain","methods","chapter",
            "makes","protection","policies","loan","wide","beauty","manager","india","position","taken","sort",
            "listings","models","michael","known","half","cases","step","engineering","florida","simple","quick",
            "none","wireless","license","paul","friday","lake","whole","annual","published","later","basic","sony",
            "shows","corporate","google","church","method","purchase","customers","active","response","practice",
            "hardware","figure","materials","fire","holiday","chat","enough","designed","along","among","death",
            "writing","speed","html","countries","loss","face","brand","discount","higher","effects","created",
            "remember","standards","oil","bit","yellow","political","increase","advertise","kingdom","base","near",
            "environmental","thought","stuff","french","storage","japan","doing","shoes","entry","stay","nature",
            "orders","availability","africa","summary","turn","mean","growth","notes","agency","king","monday",
            "european","activity","copy","although","drug","pics","western","income","force","cash","employment",
            "overall","bay","river","commission","package","contents","seen","players","engine","port","album",
            "regional","stop","supplies","started","administration","bar","institute","views","plans","double","dog",
            "build","screen","exchange","types","soon","sponsored","lines","electronic","continue","across","benefits",
            "needed","season","apply","someone","held","anything","printer","condition","effective","believe",
            "organization","effect","asked","mind","sunday","selection","lost","tour","menu","volume","cross",
            "anyone","mortgage","hope","silver","corporation","wish","inside","solution","mature","role","rather",
            "weeks","addition","came","supply","nothing","certain","executive","running","lower","necessary","union",
            "jewelry","according","clothing","particular","fine","names","robert","homepage","hour","gas","skills",
            "six","islands","advice","career","military","rental","decision","leave","british","teens","huge","woman",
            "facilities","zip","bid","kind","sellers","middle","move","cable","opportunities","taking","values",
            "division","coming","tuesday","object","appropriate","machine","logo","length","actually","nice","score",
            "statistics","client","ok","returns","capital","follow","sample","investment","sent","shown","saturday",
            "christmas","england","culture","band","flash","lead","george","choice","went","starting","registration",
            "courses","consumer","airport","foreign","artist","outside","furniture","levels","channel","letter","mode",
            "phones","ideas","wednesday","structure","fund","summer","allow","degree","contract","button","releases",
            "homes","super","male","matter","custom","almost","took","located","multiple","asian","distribution",
            "editor","industrial","cause","potential","song","focus","late","fall","featured","idea","rooms","female",
            "responsible","win","associated","thomas","primary","cancer","numbers","reason","tool","browser","spring",
            "foundation","answer","voice","friendly","schedule","documents","communication","purpose","feature","bed",
            "comes","police","everyone","independent","approach","cameras","brown","physical","operating","hill","maps",
            "medicine","deal","hold","ratings","chicago","forms","glass","happy","smith","wanted","developed","thank",
            "safe","unique","survey","prior","telephone","sport","ready","feed","animal","sources","mexico","population",
            "regular","secure","navigation","operations","therefore","simply","evidence","station","christian",
            "round","favorite","understand","option","master","valley","recently","probably","sea","built",
            "publications","blood","cut","worldwide","improve","connection","publisher","hall","larger","anti",
            "networks","earth","parents","impact","transfer","introduction","kitchen","strong","carolina","wedding",
            "properties","hospital","ground","overview","ship","accommodation","owners","disease","excellent","paid",
            "italy","perfect","hair","opportunity","kit","classic","basis","command","cities","william","express",
            "award","distance","tree","peter","assessment","ensure","thus","wall","involved","extra","especially",
            "interface","partners","budget","rated","guides","success","maximum","operation","existing","quite",
            "selected","boy","amazon","patients","restaurants","beautiful","warning","wine","locations","horse","vote",
            "forward","flowers","stars","significant","lists","technologies","owner","retail","animals","useful",
            "directly","manufacturer","ways","son","providing","rule","housing","takes","bring","catalog","max",
            "trying","mother","authority","considered","told","traffic","programme","joined","input","strategy",
            "feet","agent","valid","bin","modern","senior","ireland","teaching","door","grand","testing","trial",
            "charge","units","instead","canadian","cool","normal","wrote","enterprise","ships","entire","educational",
            "leading","metal","positive","fitness","chinese","opinion","asia","football","abstract","uses","output",
            "funds","greater","likely","develop","employees","artists","alternative","processing","responsibility",
            "resolution","java","guest","seems","publication","pass","relations","trust","van","contains","session",
            "multi","photography","republic","fees","components","vacation","century","academic","assistance",
            "completed","skin","graphics","indian","expected","ring","grade","dating","pacific","mountain",
            "organizations","filter","mailing","vehicle","longer","consider","northern","behind","panel","floor",
            "german","buying","match","proposed","default","require","boys","outdoor","deep","morning","otherwise",
            "allows","rest","protein","plant","reported","hit","transportation","pool","mini","politics","partner",
            "authors","boards","faculty","parties","fish","membership","mission","eye","string","sense","modified",
            "pack","released","stage","internal","goods","recommended","born","unless","richard","detailed",
            "japanese","race","approved","background","target","except","character","maintenance","ability","maybe",
            "functions","moving","brands","places","pretty","yourself","winter","battery","youth","pressure","debt",
            "keywords","medium","television","interested","core","break","purposes","throughout","sets","dance",
            "wood","itself","defined","papers","playing","awards","fee","studio","reader","virtual","device",
            "established","answers","rent","remote","dark","programming","external","apple","regarding",
            "instructions","offered","theory","enjoy","remove","aid","surface","minimum","visual","host","variety",
            "teachers","manual","block","subjects","agents","increased","repair","fair","civil","steel",
            "understanding","songs","fixed","wrong","beginning","hands","associates","finally","updates","desktop",
            "classes","gets","sector","capacity","requires","fat","fully","father","electric","saw","instruments",
            "quotes","officer","driver","businesses","dead","respect","unknown","specified","restaurant","trip",
            "worth","procedures","poor","teacher","eyes","relationship","workers","farm","peace","traditional",
            "campus","showing","creative","coast","benefit","progress","funding","devices","lord","grant","agree",
            "fiction","hear","sometimes","watches","careers","beyond","goes","families","led","museum","themselves",
            "fan","transport","interesting","blogs","wife","evaluation","accepted","former","implementation","ten",
            "hits","zone","complex","cat","galleries","references","die","presented","jack","flat","flow","agencies",
            "literature","respective","parent","spanish","michigan","setting","scale","stand","economy","highest",
            "helpful","monthly","critical","frame","musical","definition","secretary","networking","path","australian",
            "employee","chief","gives","bottom","magazines","packages","detail","laws","changed","pet","heard","begin",
            "individuals","royal","clean","switch","russian","largest","african","guy","titles","relevant",
            "guidelines","justice","connect","bible","cup","basket","applied","weekly","installation","described",
            "demand","suite","square","chris","attention","advance","skip","diet","army","gear","lee","difference",
            "allowed","correct","charles","nation","selling","lots","piece","sheet","firm","seven","older","species",
            "jump","module","resort","facility","random","pricing","certificate","minister","motion","looks",
            "fashion","directions","visitors","documentation","monitor","trading","forest","calls","whose","coverage",
            "couple","giving","chance","vision","ball","ending","clients","actions","listen","discuss","accept",
            "automotive","goal","successful","sold","wind","communities","clinical","situation","sciences","markets",
            "lowest","highly","publishing","appear","emergency","developing","lives","currency","leather","determine",
            "temperature","patient","actual","historical","stone","commerce","perhaps","persons","difficult",
            "scientific","satellite","fit","tests","village","accounts","met","pain","factors","coffee","settings",
            "buyer","cultural","easily","edge","functional","root","closed","holidays","ice","pink","balance",
            "monitoring","graduate","replies","shot","architecture","initial","label","thinking","recommend",
            "league","waste","minute","bus","provider","optional","dictionary","cold","accounting","manufacturing",
            "sections","chair","fishing","effort","phase","fields","bag","fantasy","letters","motor","professor",
            "context","install","shirt","apparel","generally","continued","foot","mass","crime","count","breast",
            "techniques","quickly","dollars","websites","religion","claim","driving","permission","surgery","patch",
            "heat","wild","measures","generation","kansas","miss","chemical","doctor","task","reduce","brought",
            "himself","nor","component","enable","exercise","bug","leader","diamond","alone","meetings","seconds",
            "keyword","interests","flight","congress","fuel","username","walk","produced","italian","paperback",
            "wait","supported","pocket","saint","rose","freedom","argument","competition","creating","joint",
            "premium","providers","fresh","characters","attorney","upgrade","factor","growing","thousands","stream",
            "apartments","pick","hearing","eastern","auctions","therapy","entries","dates","generated","signed",
            "upper","administrative","serious","prime","limit","began","steps","errors","shops","efforts","informed",
            "thoughts","worked","quantity","urban","practices","sorted","reporting","essential","myself","tours",
            "platform","load","affiliate","labor","immediately","admin","nursing","defense","machines","designated",
            "tags","heavy","covered","recovery","integrated","configuration","comprehensive","expert","universal",
            "protect","drop","solid","presentation","languages","became","orange","compliance","vehicles","prevent",
            "theme","rich","campaign","marine","improvement","guitar","finding","examples","spirit","claims",
            "challenge","acceptance","strategies","seem","affairs","touch","intended","towards","goals","hire",
            "election","suggest","branch","charges","serve","affiliates","reasons","magic","mount","smart","talking",
            "gave","ones","latin","multimedia","avoid","certified","manage","corner","rank","computing","element",
            "birth","virus","abuse","interactive","requests","separate","quarter","procedure","leadership","tables",
            "define","racing","religious","facts","breakfast","column","plants","faith","chain","developer",
            "identify","avenue","missing","died","approximately","domestic","recommendations","moved","reach",
            "comparison","mental","viewed","moment","extended","sequence","inch","attack","sorry","centers",
            "opening","damage","lab","reserve","recipes","gamma","plastic","produce","snow","placed","truth",
            "counter","failure","follows","weekend","dollar","camp","automatically","minnesota","films","bridge",
            "native","fill","williams","movement","printing","baseball","owned","approval","draft","chart","played",
            "contacts","jesus","readers","clubs","equal","adventure","matching","offering","shirts","profit",
            "leaders","posters","institutions","assistant","variable","advertisement","expect","parking",
            "headlines","yesterday","compared","determined","wholesale","workshop","russia","gone","codes","kinds",
            "extension","seattle","statements","golden","completely","teams","fort","lighting","senate","forces",
            "funny","brother","gene","turned","portable","tried","electrical","applicable","disc","returned",
            "pattern","boat","named","theatre","laser","earlier","manufacturers","sponsor","classical","icon",
            "warranty","dedicated","indiana","direction","harry","basketball","objects","ends","delete","evening",
            "assembly","nuclear","taxes","mouse","signal","criminal","issued","brain","wisconsin","powerful","dream",
            "obtained","false","cast","flower","felt","personnel","passed","supplied","identified","falls","soul",
            "opinions","promote","stated","stats","hawaii","professionals","appears","carry","flag","decided",
            "covers","advantage","hello","designs","maintain","tourism","priority","newsletters","adults","clips",
            "savings","graphic","atom","payments","estimated","binding","brief","ended","winning","eight",
            "anonymous","iron","straight","script","served","wants","miscellaneous","prepared","void","dining",
            "alert","integration","atlanta","dakota","tag","interview","mix","framework","disk","installed","queen",
            "credits","clearly","fix","handle","sweet","desk","criteria","dave","massachusetts","hong","vice",
            "associate","truck","behavior","enlarge","ray","frequently","revenue","measure","changing","votes",
            "duty","looked","discussions","bear","gain","festival","laboratory","ocean","flights","experts","signs",
            "lack","depth","whatever","logged","laptop","vintage","train","exactly","dry","explore","maryland",
            "concept","nearly","eligible","checkout","reality","forgot","handling","origin","knew","gaming",
            "feeds","billion","destination","scotland","faster","intelligence","dallas","bought","nations","route",
            "followed","specifications","broken","frank","alaska","zoom","blow","battle","residential","anime",
            "speak","decisions","industries","protocol","query","clip","partnership","editorial","expression",
            "equity","provisions","speech","wire","principles","suggestions","rural","shared","sounds","replacement",
            "strategic","judge","spam","economics","acid","bytes","cent","forced","compatible","fight","apartment",
            "height","null","zero","speaker","filed","netherlands","obtain","consulting","recreation","offices",
            "designer","remain","managed","failed","marriage","roll","korea","banks","participants","secret","bath",
            "kelly","leads","negative","austin","favorites","toronto","theater","springs","missouri","andrew","perform",
            "healthy","translation","estimates","font","assets","injury","joseph","ministry","drivers","lawyer",
            "figures","married","protected","proposal","sharing","philadelphia","portal","waiting","birthday","beta",
            "fail","banking","officials","brian","toward","won","slightly","assist","conduct","contained",
            "legislation","calling","parameters","jazz","serving","bags","profiles","miami","comics","matters",
            "houses","doc","postal","relationships","tennessee","wear","controls","breaking","combined","ultimate",
            "wales","representative","frequency","introduced","minor","finish","departments","residents","noted",
            "displayed","mom","reduced","physics","rare","spent","performed","extreme","samples","davis","daniel",
            "bars","reviewed","row","forecast","removed","helps","singles","administrator","cycle","amounts",
            "contain","accuracy","dual","rise","sleep","bird","pharmacy","brazil","creation","static","scene",
            "hunter","addresses","lady","crystal","famous","writer","chairman","violence","fans","oklahoma",
            "speakers","drink","academy","dynamic","gender","eat","permanent","agriculture","dell","cleaning",
            "portfolio","practical","delivered","collectibles","infrastructure","exclusive","seat","concerns",
            "colour","vendor","originally","intel","utilities","philosophy","regulation","officers","reduction",
            "aim","bids","referred","supports","nutrition","recording","regions","junior","toll","cape","ann",
            "rings","meaning","tip","secondary","wonderful","mine","ladies","henry","ticket","announced","guess",
            "agreed","prevention","whom","ski","soccer","math","import","posting","presence","instant","mentioned",
            "automatic","healthcare","viewing","maintained","increasing","majority","connected","christ","dan","dogs",
            "directors","aspects","austria","ahead","moon","participation","scheme","utility","preview","fly",
            "manner","matrix","containing","combination","amendment","despite","strength","guaranteed","turkey",
            "libraries","proper","distributed","degrees","singapore","enterprises","delta","fear","seeking",
            "inches","phoenix","convention","shares","principal","daughter","standing","comfort","colors","wars",
            "cisco","ordering","kept","alpha","appeal","cruise","bonus","certification","previously","hey",
            "bookmark","buildings","specials","beat","disney","household","batteries","adobe","smoking","becomes",
            "drives","arms","alabama","tea","improved","trees","achieve","positions","dress","subscription","dealer",
            "contemporary","sky","utah","nearby","carried","happen","exposure","panasonic","hide","permalink",
            "signature","gambling","refer","miller","provision","outdoors","clothes","caused","luxury","frames",
            "certainly","indeed","newspaper","toy","circuit","layer","printed","slow","removal","easier","liability",
            "trademark","hip","printers","faqs","nine","adding","kentucky","mostly","eric","spot","taylor",
            "trackback","prints","spend","factory","interior","revised","grow","americans","optical","promotion",
            "relative","amazing","clock","dot","identity","suites","conversion","feeling","hidden","reasonable",
            "victoria","serial","relief","revision","broadband","influence","ratio","importance","rain","onto",
            "planet","webmaster","copies","recipe","permit","seeing","proof","tennis","bass","prescription",
            "bedroom","empty","instance","hole","pets","ride","licensed","orlando","specifically","tim","bureau",
            "maine","sql","represent","conservation","pair","ideal","specs","recorded","don","pieces","finished",
            "parks","dinner","lawyers","sydney","stress","cream","runs","trends","yeah","discover","patterns",
            "boxes","louisiana","hills","javascript","fourth","advisor","marketplace","evil","aware","wilson",
            "shape","evolution","irish","certificates","objectives","stations","suggested","gps","remains","greatest",
            "firms","concerned","euro","operator","structures","generic","encyclopedia","usage","cap","ink","charts",
            "continuing","mixed","census","peak","competitive","exist","wheel","transit","suppliers","salt",
            "compact","poetry","lights","tracking","angel","bell","keeping","preparation","attempt","receiving",
            "matches","accordance","width","noise","engines","forget","array","discussed","accurate","stephen",
            "elizabeth","climate","reservations","pin","playstation","alcohol","greek","instruction","managing",
            "annotation","sister","raw","differences","walking","explain","smaller","newest","establish","gnu",
            "happened","expressed","jeff","extent","sharp","ben","lane","paragraph","kill","mathematics",
            "compensation","export","managers","aircraft","modules","sweden","conflict","conducted","versions",
            "employer","occur","percentage","knows","mississippi","describe","concern","backup","requested",
            "citizens","connecticut","heritage","personals","immediate","holding","trouble","spread","coach",
            "kevin","agricultural","expand","supporting","audience","assigned","jordan","collections","ages",
            "participate","plug","specialist","cook","affect","virgin","experienced","investigation","raised","hat",
            "institution","directed","dealers","searching","sporting","helping","perl","affected","lib","bike",
            "totally","plate","expenses","indicate","blonde","proceedings","favourite","transmission","anderson",
            "utc","characteristics","der","lose","organic","seek","experiences","albums","cheats","extremely",
            "contracts","guests","hosted","diseases","concerning","developers","equivalent","chemistry","tony",
            "neighborhood","nevada","kits","thailand","variables","agenda","anyway","continues","tracks","advisory",
            "cam","curriculum","logic","template","prince","circle","soil","grants","anywhere","psychology",
            "responses","atlantic","wet","circumstances","edward","investor","identification","ram","leaving",
            "wildlife","appliances","matt","elementary","cooking","speaking","sponsors","fox","unlimited","respond",
            "sizes","plain","exit","entered","iran","arm","keys","launch","wave","checking","costa","belgium",
            "printable","holy","acts","guidance","mesh","trail","enforcement","symbol","crafts","highway","buddy",
            "hardcover","observed","dean","setup","poll","booking","glossary","fiscal","celebrity","styles",
            "denver","unix","filled","bond","channels","appendix","notify","blues","chocolate","pub","portion",
            "scope","supplier","cables","cotton","bluetooth","controlled","requirement","authorities","biology",
            "dental","killed","border","ancient","debate","representatives","starts","pregnancy","causes","arkansas",
            "biography","leisure","attractions","learned","transactions","notebook","explorer","historic","attached",
            "opened","husband","disabled","authorized","crazy","upcoming","britain","concert","retirement","scores",
            "financing","efficiency","comedy","adopted","efficient","weblog","linear","commitment","specialty",
            "bears","jean","hop","carrier","edited","constant","visa","mouth","jewish","meter","linked","portland",
            "interviews","concepts","gun","reflect","pure","deliver","wonder","hell","lessons","fruit","begins",
            "qualified","reform","lens","alerts","treated","discovery","draw","mysql","classified","relating",
            "assume","confidence","alliance","confirm","warm","neither","lewis","howard","offline","leaves",
            "engineer","lifestyle","consistent","replace","clearance","connections","inventory","converter",
            "organisation","checks","reached","becoming","safari","objective","indicated","sugar","crew","legs",
            "stick","securities","allen","relation","enabled","genre","slide","montana","volunteer","tested","rear",
            "democratic","enhance","switzerland","exact","bound","parameter","adapter","processor","node","formal",
            "dimensions","contribute","lock","hockey","storm","micro","colleges","laptops","mile","showed",
            "challenges","editors","threads","bowl","supreme","brothers","recognition","presents","ref","tank",
            "submission","dolls","estimate","encourage","navy","kid","regulatory","inspection","consumers","cancel",
            "limits","territory","transaction","manchester","continue","scroll","relate","divisions","swim","wages",
            "additionally","suffer","forests","fellowship","invalid","concerts","martial","males","victorian",
            "retain","colours","execute","tunnel","genres","cambodia","patents","copyrights","chaos","lithuania",
            "mastercard","wheat","chronicles","obtaining","beaver","updating","distribute","readings","decorative",
            "confused","compiler","enlargement","eagles","bases","accused","bee","campaigns","unity","loud",
            "conjunction","bride","rats","defines","airports","instances","indigenous","begun","brunette","packets",
            "anchor","socks","validation","parade","corruption","stat","trigger","incentives","cholesterol",
            "gathered","essex","slovenia","notified","differential","beaches","folders","dramatic","surfaces",
            "terrible","routers","cruz","pendant","dresses","baptist","scientist","hiring","clocks","arthritis",
            "bios","females","wallace","nevertheless","reflects","taxation","fever","cuisine","surely",
            "practitioners","transcript","theorem","inflation","thee","ruth","pray","stylus","compounds","pope",
            "drums","contracting","arnold","structured","reasonably","jeep","chicks","bare","hung","cattle","mba",
            "radical","graduates","rover","recommends","controlling","treasure","reload","distributors","flame",
            "tanks","assuming","monetary","elderly","pit","arlington","mono","particles","floating","extraordinary",
            "tile","indicating","bolivia","spell","hottest","stevens","coordinate","kuwait","exclusively","emily",
            "alleged","limitation","widescreen","compile","webster","struck","illustration","plymouth","warnings",
            "construct","apps","inquiries","bridal","annex","mag","gsm","inspiration","tribal","curious",
            "affecting","freight","rebate","meetup","eclipse","sudan","downloading","shuttle","aggregate",
            "stunning","cycles","affects","forecasts","detect","actively","ciao","knee","prep","complicated",
            "fastest","butler","injured","decorating","payroll","cookbook","expressions","courier","uploaded",
            "shakespeare","hints","collapse","americas","connectors","unlikely","pros","conflicts","beverage",
            "tribute","wired","elvis","immune","travelers","forestry","barriers","rarely","infected","offerings",
            "martha","genesis","barrier","argue","incorrect","trains","metals","bicycle","furnishings","letting",
            "arise","guatemala","celtic","thereby","jamie","particle","perception","minerals","advise","humidity",
            "bottles","boxing","bangkok","renaissance","pathology","sara","bra","ordinance","hughes","photographers",
            "infections","jeffrey","chess","operates","brisbane","configured","survive","oscar","festivals","menus",
            "joan","possibilities","duck","reveal","canal","amino","contributing","herbs","clinics","cow",
            "manitoba","analytical","missions","watson","lying","costumes","strict","dive","saddam","circulation",
            "drill","offense","bryan","protest","assumption","jerusalem","hobby","tries","invention","nickname"
        };

        public static Lexicon Load(string dataDir, Diagnostics diag)
        {
            Lexicon lex = new Lexicon();
            bool loaded = false;
            try
            {
                if (!string.IsNullOrEmpty(dataDir))
                {
                    string wordsPath = Path.Combine(dataDir, "words.tsv");
                    string phrasesPath = Path.Combine(dataDir, "phrases.tsv");
                    string bigramsPath = Path.Combine(dataDir, "word_bigrams.tsv");
                    if (File.Exists(wordsPath)) loaded = lex.LoadWords(wordsPath, diag) || loaded;
                    if (File.Exists(phrasesPath)) lex.LoadPhrases(phrasesPath, diag);
                    if (File.Exists(bigramsPath)) lex.LoadBigrams(bigramsPath, diag);
                }
            }
            catch (Exception ex)
            {
                if (diag != null) diag.Error("Lexicon.Load 异常: " + ex.Message);
            }

            if (lex.Words.Count == 0)
            {
                if (diag != null) diag.Warn("未加载到 words.tsv，使用内置兜底词表 " + FallbackWords.Length + " 个");
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                int rank = FallbackWords.Length;
                for (int i = 0; i < FallbackWords.Length; i++)
                {
                    string w = FallbackWords[i];
                    if (!seen.Add(w)) continue;
                    lex.Words.Add(new WordEntry { Word = w, Score = Math.Max(1, rank - i) * 137 });
                }
            }

            int maxW = 1;
            foreach (WordEntry w in lex.Words) if (w.Score > maxW) maxW = w.Score;
            lex.MaxWordScore = maxW;

            int maxP = 1;
            foreach (PhraseEntry p in lex.Phrases) if (p.Score > maxP) maxP = p.Score;
            lex.MaxPhraseScore = maxP;
            lex.LoadedFromFile = loaded;
            return lex;
        }

        bool LoadWords(string path, Diagnostics diag)
        {
            int lineNo = 0;
            int added = 0;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (string raw in File.ReadLines(path))
                {
                    lineNo++;
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 1) continue;
                    string w = parts[0].Trim().ToLowerInvariant();
                    if (!IsValidWord(w)) continue;
                    if (!seen.Add(w)) continue;
                    int score = 1;
                    if (parts.Length >= 2) int.TryParse(parts[1].Trim(), out score);
                    if (score <= 0) score = 1;
                    Words.Add(new WordEntry { Word = w, Score = score });
                    added++;
                }
            }
            catch (Exception ex)
            {
                if (diag != null) diag.Error("读取 words.tsv 失败: " + ex.Message);
            }
            if (diag != null) diag.Info("words.tsv: 读取 " + added + " 个词（共 " + lineNo + " 行）");
            return added > 0;
        }

        void LoadPhrases(string path, Diagnostics diag)
        {
            int added = 0;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] parts = line.Split('\t');
                    string p = parts[0].Trim().ToLowerInvariant();
                    if (p.Length < 3 || p.Length > 80) continue;
                    string[] tokens = p.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 2 || tokens.Length > 6) continue;
                    bool ok = true;
                    foreach (string t in tokens) if (!IsValidWord(t)) { ok = false; break; }
                    if (!ok) continue;
                    if (!seen.Add(p)) continue;
                    int score = 1;
                    if (parts.Length >= 2) int.TryParse(parts[1].Trim(), out score);
                    if (score <= 0) score = 1;
                    Phrases.Add(new PhraseEntry { Phrase = p, FirstToken = tokens[0], Score = score });
                    added++;
                }
            }
            catch (Exception ex)
            {
                if (diag != null) diag.Error("读取 phrases.tsv 失败: " + ex.Message);
            }
            if (diag != null) diag.Info("phrases.tsv: 读取 " + added + " 条短语");
        }

        void LoadBigrams(string path, Diagnostics diag)
        {
            int added = 0;
            try
            {
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    string a = parts[0].Trim().ToLowerInvariant();
                    string b = parts[1].Trim().ToLowerInvariant();
                    if (!IsValidWord(a) || !IsValidWord(b)) continue;
                    int score = 1;
                    if (parts.Length >= 3) int.TryParse(parts[2].Trim(), out score);
                    if (score <= 0) score = 1;
                    List<NextEntry> list;
                    if (!NextByWord.TryGetValue(a, out list)) { list = new List<NextEntry>(); NextByWord[a] = list; }
                    if (list.Count < 24) list.Add(new NextEntry { Next = b, Insert = b, Score = score, IsPhrase = false });
                    added++;
                }
            }
            catch (Exception ex)
            {
                if (diag != null) diag.Error("读取 word_bigrams.tsv 失败: " + ex.Message);
            }
            if (diag != null) diag.Info("word_bigrams.tsv: 读取 " + added + " 条二元组");
        }

        public static bool IsValidWord(string w)
        {
            if (string.IsNullOrEmpty(w)) return false;
            if (w.Length < 2 || w.Length > 30) return false;
            bool lastSep = true;
            int letters = 0;
            foreach (char c in w)
            {
                if (c >= 'a' && c <= 'z') { lastSep = false; letters++; }
                else if (c == '\'' || c == '-')
                {
                    if (lastSep) return false;
                    lastSep = true;
                }
                else return false;
            }
            if (lastSep) return false;
            return letters >= 2;
        }
    }
}
